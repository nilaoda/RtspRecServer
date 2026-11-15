using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

internal static class RecordingUrlBuilder
{
    public static string Build(RecordingTask task)
    {
        var url = task.Url;
        if (url.Contains("playseek=", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        if (url.Contains("/PLTV/", StringComparison.OrdinalIgnoreCase))
        {
            var startLocal = task.StartTime.ToLocalTime().ToString("yyyyMMddHHmmss");
            var endLocal = task.EndTime.ToLocalTime().AddMinutes(1).ToString("yyyyMMddHHmmss");
            var separator = url.Contains('?') ? '&' : '?';
            return $"{url}{separator}playseek={startLocal}-{endLocal}";
        }

        if (url.Contains("starttime=", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var startUtc = task.StartTime.ToUniversalTime().ToString("yyyyMMddTHHmmssZ");
        var endUtc = task.EndTime.ToUniversalTime().AddMinutes(1).ToString("yyyyMMddTHHmmssZ");
        var querySeparator = url.Contains('?') ? '&' : '?';
        return $"{url}{querySeparator}starttime={startUtc}&endtime={endUtc}";
    }
}
