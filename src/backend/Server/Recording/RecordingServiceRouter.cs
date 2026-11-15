using RtspRecServer.Shared;

namespace RtspRecServer.Server.Recording;

public sealed class RecordingServiceRouter : IRecordingService
{
    private readonly RtspRecordingService _rtspRecordingService;
    private readonly HttpLiveRecordingService _httpLiveRecordingService;
    private readonly UdpLiveRecordingService _udpLiveRecordingService;

    public RecordingServiceRouter(
        RtspRecordingService rtspRecordingService,
        HttpLiveRecordingService httpLiveRecordingService,
        UdpLiveRecordingService udpLiveRecordingService)
    {
        _rtspRecordingService = rtspRecordingService;
        _httpLiveRecordingService = httpLiveRecordingService;
        _udpLiveRecordingService = udpLiveRecordingService;
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
