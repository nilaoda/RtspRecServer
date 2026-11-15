namespace RtspRecServer.Server.Recording.TsAnalysis;

internal static class TsUtil
{
    public const int TsPacketSize = 188;
    public const byte TsSyncByte = 0x47;

    internal static int FindPacketSync(ReadOnlySpan<byte> data, int stride = TsPacketSize, int prefixSize = 0)
    {
        var limit = data.Length - stride * 3 - prefixSize;
        for (var index = 0; index < limit; index++)
        {
            var sync = index + prefixSize;
            if (data[sync] == TsSyncByte &&
                data[sync + stride] == TsSyncByte &&
                data[sync + stride * 2] == TsSyncByte)
            {
                return sync;
            }
        }

        return -1;
    }
}
