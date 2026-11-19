using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

public sealed class WebSocketConnectionManager
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);

    private sealed class Connection
    {
        public required WebSocket Socket { get; init; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();

    public Guid Register(WebSocket socket)
    {
        var id = Guid.NewGuid();
        _connections.TryAdd(id, new Connection { Socket = socket });
        return id;
    }

    public async Task HandleConnectionAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var id = Register(socket);
        var buffer = new byte[4 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch
        {
        }
        finally
        {
            if (_connections.TryRemove(id, out var connection))
            {
                using var timeout = new CancellationTokenSource(SendTimeout);
                try
                {
                    await connection.SendLock.WaitAsync(timeout.Token);
                    try
                    {
                        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        {
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", timeout.Token);
                        }
                    }
                    finally
                    {
                        connection.SendLock.Release();
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
                {
                    socket.Abort();
                }
            }
        }
    }

    public Task BroadcastTaskAsync(RecordingTaskDto task, CancellationToken cancellationToken)
    {
        var message = new TaskUpdateMessage { Task = task };
        return BroadcastAsync(message, RtspJsonContext.Default.TaskUpdateMessage, cancellationToken);
    }

    public Task BroadcastSystemStatusAsync(SystemStatus status, CancellationToken cancellationToken)
    {
        var message = new SystemStatusMessage { Status = status };
        return BroadcastAsync(message, RtspJsonContext.Default.SystemStatusMessage, cancellationToken);
    }

    public Task BroadcastTaskErrorLogAsync(TaskErrorLogMessage message, CancellationToken cancellationToken)
    {
        return BroadcastAsync(message, RtspJsonContext.Default.TaskErrorLogMessage, cancellationToken);
    }

    private Task BroadcastAsync<T>(T message, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        if (_connections.IsEmpty || cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        var payload = new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(message, typeInfo));
        return Task.WhenAll(_connections.Select(item => SendAsync(item.Key, item.Value, payload, cancellationToken)));
    }

    private async Task SendAsync(Guid id, Connection connection, ArraySegment<byte> payload, CancellationToken cancellationToken)
    {
        var socket = connection.Socket;
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        // 每个客户端独立发送，慢客户端只能占用有限的调度时间。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SendTimeout);
        var lockTaken = false;
        try
        {
            await connection.SendLock.WaitAsync(timeout.Token);
            lockTaken = true;
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !lockTaken)
        {
        }
        catch
        {
            if (_connections.TryRemove(id, out _))
            {
                socket.Abort();
            }
        }
        finally
        {
            if (lockTaken)
            {
                connection.SendLock.Release();
            }
        }
    }
}
