using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

public sealed class WebSocketRecordingNotifier : IRecordingNotifier
{
    private readonly WebSocketConnectionManager _connections;

    public WebSocketRecordingNotifier(WebSocketConnectionManager connections)
    {
        _connections = connections;
    }

    public Task NotifyTaskUpdateAsync(RecordingTaskDto task, CancellationToken cancellationToken)
    {
        return _connections.BroadcastTaskAsync(task, cancellationToken);
    }
}
