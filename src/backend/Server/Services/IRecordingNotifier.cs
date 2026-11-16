using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

public interface IRecordingNotifier
{
    Task NotifyTaskUpdateAsync(RecordingTaskDto task, CancellationToken cancellationToken);
    Task NotifyTaskErrorLogAsync(TaskErrorLogMessage message, CancellationToken cancellationToken);
}

public sealed class NullRecordingNotifier : IRecordingNotifier
{
    public Task NotifyTaskUpdateAsync(RecordingTaskDto task, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task NotifyTaskErrorLogAsync(TaskErrorLogMessage message, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
