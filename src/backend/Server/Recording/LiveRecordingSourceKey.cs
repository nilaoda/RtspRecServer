using RtspRecServer.Shared;

namespace RtspRecServer.Server.Recording;

internal readonly record struct LiveRecordingSourceKey(string? HttpUrl, DirectUdpSource? UdpSource)
{
    public static LiveRecordingSourceKey? FromTask(RecordingTask task)
    {
        if (!task.IsLive)
        {
            return null;
        }

        if (LiveSourceClassifier.IsHttpLive(task.Url))
        {
            // 查询参数可能选择不同的流，保留完整 URL。
            return new LiveRecordingSourceKey(task.Url.Trim(), null);
        }

        return DirectUdpSourceParser.TryParse(task.Url, out var udpSource, out _)
            ? new LiveRecordingSourceKey(null, udpSource)
            : null;
    }
}
