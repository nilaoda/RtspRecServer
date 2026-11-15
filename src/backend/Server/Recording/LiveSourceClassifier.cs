namespace RtspRecServer.Server.Recording;

internal static class LiveSourceClassifier
{
    public static bool IsLive(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var value = url.Trim();
        if (IsDirectLive(value))
        {
            return true;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
        {
            return uri.AbsolutePath.Contains("/rtp/", StringComparison.OrdinalIgnoreCase) ||
                   uri.AbsolutePath.Contains("/udp/", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public static bool IsDirectLive(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var value = url.Trim();
        return value.StartsWith("rtp://", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("udp://", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHttpLive(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var value = url.Trim();
        return IsLive(value) &&
               (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }
}
