using RtspRecServer.Shared;

namespace RtspRecServer.Server.Recording;

public interface IRecordingService
{
    Task<RecordingResult> RecordAsync(
        RecordingTask task,
        string outputPath,
        TimeSpan? targetDuration,
        string recordingTransport,
        Action<RecordingProgress> onProgress,
        CancellationToken cancellationToken);
}
