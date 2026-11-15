namespace RtspRecServer.Server.Recording.TsAnalysis;

/// <summary>
/// 录制链路只处理 188 字节 TS；保留与 TSCutter 相同的同步探测窗口和基本语义。
/// </summary>
public readonly record struct TsPacketLayout(int PacketSize, long SyncOffset)
{
    internal const int SyncProbePacketCount = 4;
    internal const int SyncProbeOverlapBytes = 204 * SyncProbePacketCount;

    public bool IsM2ts => false;
    public int PrefixSize => 0;
    public long PacketOffset => SyncOffset;

    public static TsPacketLayout FromPacketOffset(int packetSize, long packetOffset) =>
        new(packetSize, packetOffset);

    internal int FindPacketSync(ReadOnlySpan<byte> data) =>
        TsUtil.FindPacketSync(data, PacketSize, PrefixSize);

    public ReadOnlySpan<byte> GetPacketBody(ReadOnlySpan<byte> buffer, int packetOffset = 0) =>
        buffer.Slice(packetOffset + PrefixSize, TsUtil.TsPacketSize);

    public void RequireSupported(bool standardOnly = false)
    {
        if (PacketSize != TsUtil.TsPacketSize)
        {
            throw new NotSupportedException("仅支持 188 字节 TS");
        }
    }

    internal static (int PacketSize, int SyncOffset) FindLayout(ReadOnlySpan<byte> data, bool validateHeaders = true)
    {
        for (var offset = 0; offset + TsUtil.TsPacketSize * (SyncProbePacketCount - 1) < data.Length; offset++)
        {
            if (data[offset] != TsUtil.TsSyncByte)
            {
                continue;
            }

            var valid = true;
            for (var index = 0; index < SyncProbePacketCount; index++)
            {
                if (data[offset + TsUtil.TsPacketSize * index] != TsUtil.TsSyncByte ||
                    (validateHeaders &&
                     !TsPacketParser.Parse(data.Slice(offset + TsUtil.TsPacketSize * index, TsUtil.TsPacketSize)).IsValid))
                {
                    valid = false;
                    break;
                }
            }

            if (valid)
            {
                return (TsUtil.TsPacketSize, offset);
            }
        }

        return (0, -1);
    }

    internal static (int PacketSize, int SyncOffset) FindShortLayout(ReadOnlySpan<byte> data)
    {
        for (var offset = Math.Max(0, data.Length - SyncProbeOverlapBytes);
             offset + TsUtil.TsPacketSize <= data.Length;
             offset++)
        {
            if (data[offset] != TsUtil.TsSyncByte)
            {
                continue;
            }

            var count = Math.Min(SyncProbePacketCount, (data.Length - offset - TsUtil.TsPacketSize) / TsUtil.TsPacketSize + 1);
            if (count < 2)
            {
                continue;
            }

            var valid = true;
            for (var index = 1; index < count; index++)
            {
                if (data[offset + index * TsUtil.TsPacketSize] != TsUtil.TsSyncByte)
                {
                    valid = false;
                    break;
                }
            }

            if (valid)
            {
                return (TsUtil.TsPacketSize, offset);
            }
        }

        for (var offset = Math.Max(0, data.Length - SyncProbeOverlapBytes);
             offset + TsUtil.TsPacketSize <= data.Length;
             offset++)
        {
            if (data[offset] != TsUtil.TsSyncByte)
            {
                continue;
            }

            if (TsPacketParser.Parse(data.Slice(offset, TsUtil.TsPacketSize)).IsValid)
            {
                return (TsUtil.TsPacketSize, offset);
            }
        }

        return default;
    }
}
