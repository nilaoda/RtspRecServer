using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

public sealed class WebSocketConnectionManager
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _connections = new();

    public Guid Register(WebSocket socket)
    {
        var id = Guid.NewGuid();
        _connections.TryAdd(id, socket);
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
            var socket = item.Value;
            if (socket.State != WebSocketState.Open)
            {
                _connections.TryRemove(item.Key, out _);
                continue;
            }

            try
            {
                await socket.SendAsync(segment, WebSocketMessageType.Text, true, cancellationToken);
            }
            catch
            {
                _connections.TryRemove(item.Key, out _);
            }
        }
    }
}
