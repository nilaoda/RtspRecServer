namespace RtspRecServer.Server.Recording;

internal static class MpegTsValidator
{
    public static bool LooksLikeMpegTs(ReadOnlySpan<byte> data) => FindSyncOffset(data) >= 0;

    public static int FindSyncOffset(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return -1;
        }

        if (data.Length < 564)
        {
            return data[0] == 0x47 ? 0 : -1;
        }

        for (var offset = 0; offset <= 188 && offset + 188 < data.Length; offset++)
        {
            if (data[offset] == 0x47 && data[offset + 188] == 0x47)
            {
                return offset;
            }
        }

        return -1;
    }
}
