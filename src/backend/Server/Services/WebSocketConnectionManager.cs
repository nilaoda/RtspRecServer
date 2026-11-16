using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

public sealed class WebSocketConnectionManager
{
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
            _connections.TryRemove(id, out _);
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
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

    private async Task BroadcastAsync<T>(T message, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        if (_connections.IsEmpty)
        {
            return;
        }

        var json = JsonSerializer.Serialize(message, typeInfo);
        var payload = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(payload);

        foreach (var item in _connections)
        {
            var connection = item.Value;
            var socket = connection.Socket;
            if (socket.State != WebSocketState.Open)
            {
                _connections.TryRemove(item.Key, out _);
                continue;
            }

            // 同一连接的多次广播可能并发（任务更新、错误日志等），必须串行发送避免帧交错
            var lockTaken = false;
            try
            {
                await connection.SendLock.WaitAsync(cancellationToken);
                lockTaken = true;
                if (socket.State != WebSocketState.Open)
                {
                    _connections.TryRemove(item.Key, out _);
                    continue;
                }

                await socket.SendAsync(segment, WebSocketMessageType.Text, true, cancellationToken);
            }
            catch
            {
                _connections.TryRemove(item.Key, out _);
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
}
