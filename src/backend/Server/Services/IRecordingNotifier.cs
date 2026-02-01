using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

public interface IRecordingNotifier
{
    Task NotifyTaskUpdateAsync(RecordingTaskDto task, CancellationToken cancellationToken);
}

public sealed class NullRecordingNotifier : IRecordingNotifier
{
    public Task NotifyTaskUpdateAsync(RecordingTaskDto task, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
