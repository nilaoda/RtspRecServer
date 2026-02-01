using RtspRecServer.Shared;

namespace RtspRecServer.Server.Recording;

public interface IRecordingService
{
    Task<RecordingResult> RecordAsync(RecordingTask task, string outputPath, Action<long> onBytesWritten, CancellationToken cancellationToken);
}
