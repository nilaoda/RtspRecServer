using RtspRecServer.Shared;

namespace RtspRecServer.Server.Recording;

public interface IRecordingService
{
    Task<RecordingResult> RecordAsync(
        RecordingTask task,
        string outputPath,
        TimeSpan? targetDuration,
        Action<long> onBytesWritten,
        CancellationToken cancellationToken);
}
