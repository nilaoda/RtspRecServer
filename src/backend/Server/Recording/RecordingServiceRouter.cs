using RtspRecServer.Shared;

namespace RtspRecServer.Server.Recording;

public sealed class RecordingServiceRouter : IRecordingService
{
    private readonly RtspRecordingService _rtspRecordingService;
    private readonly HttpLiveRecordingService _httpLiveRecordingService;

    public RecordingServiceRouter(
        RtspRecordingService rtspRecordingService,
        HttpLiveRecordingService httpLiveRecordingService)
    {
        _rtspRecordingService = rtspRecordingService;
        _httpLiveRecordingService = httpLiveRecordingService;
    }

    public Task<RecordingResult> RecordAsync(
        RecordingTask task,
        string outputPath,
        TimeSpan? targetDuration,
        string recordingTransport,
        Action<RecordingProgress> onProgress,
        CancellationToken cancellationToken)
    {
        if (task.IsLive)
        {
            if (LiveSourceClassifier.IsHttpLive(task.Url))
            {
                return _httpLiveRecordingService.RecordAsync(
                    task, outputPath, targetDuration, recordingTransport, onProgress, cancellationToken);
            }

            return Task.FromResult(new RecordingResult
            {
                Success = false,
                ErrorMessage = "当前版本暂不支持该直播源，请使用 udpxy HTTP 地址",
                OutputPath = outputPath
            });
        }

        return _rtspRecordingService.RecordAsync(
            task, outputPath, targetDuration, recordingTransport, onProgress, cancellationToken);
    }
}
