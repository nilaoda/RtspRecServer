using RtspRecServer.Shared;

namespace RtspRecServer.Server.Recording;

internal sealed class RecordingServiceRouter : IRecordingService
{
    private readonly RtspRecordingService _rtspRecordingService;
    private readonly HttpLiveRecordingService _httpLiveRecordingService;
    private readonly UdpLiveRecordingService _udpLiveRecordingService;
    private readonly SharedLiveRecordingService _sharedLiveRecordingService;

    public RecordingServiceRouter(
        RtspRecordingService rtspRecordingService,
        HttpLiveRecordingService httpLiveRecordingService,
        UdpLiveRecordingService udpLiveRecordingService,
        SharedLiveRecordingService sharedLiveRecordingService)
    {
        _rtspRecordingService = rtspRecordingService;
        _httpLiveRecordingService = httpLiveRecordingService;
        _udpLiveRecordingService = udpLiveRecordingService;
        _sharedLiveRecordingService = sharedLiveRecordingService;
    }

    public Task<RecordingResult> RecordAsync(
        RecordingTask task,
        string outputPath,
        TimeSpan? targetDuration,
        string recordingTransport,
        Action<RecordingProgress> onProgress,
        CancellationToken cancellationToken,
        Stream? output = null)
    {
        if (task.IsLive)
        {
            // 扫描与模拟录制的调用方提供输出流，保持它们独立采样。
            if (output is null && LiveRecordingSourceKey.FromTask(task) is { } sourceKey)
            {
                return _sharedLiveRecordingService.RecordAsync(task, sourceKey, outputPath, onProgress, cancellationToken);
            }

            if (LiveSourceClassifier.IsHttpLive(task.Url))
            {
                return _httpLiveRecordingService.RecordAsync(
                    task, outputPath, targetDuration, recordingTransport, onProgress, cancellationToken, output);
            }

            if (LiveSourceClassifier.IsDirectLive(task.Url))
            {
                return _udpLiveRecordingService.RecordAsync(
                    task, outputPath, targetDuration, recordingTransport, onProgress, cancellationToken, output);
            }

            return Task.FromResult(new RecordingResult
            {
                Success = false,
                ErrorMessage = "当前版本不支持该直播源协议",
                OutputPath = outputPath
            });
        }

        return _rtspRecordingService.RecordAsync(
            task, outputPath, targetDuration, recordingTransport, onProgress, cancellationToken, output);
    }
}
