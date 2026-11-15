using RtspRecServer.Server.Recording.TsAnalysis;

namespace RtspRecServer.Tests.Recording;

public class TsAnalysisTests
{
    [Fact]
    public void LiveSession_DetectsVideoAndAudioStreams()
    {
        var packets = new List<byte[]>
        {
            CreatePsiPacket(0x0000, 0, BuildPatSection(0x0100)),
            CreatePsiPacket(0x0100, 0, BuildPmtSection(
                0x0101,
                (TsStreamTypes.H264, 0x0101),
                (TsStreamTypes.Aac, 0x0102))),
            CreatePacket(0x0101, 0, pcrBase: 0),
            CreatePesPacket(0x0101, 1, 90_000),
            CreatePesPacket(0x0102, 0, 90_000, 0xC0),
            CreatePacket(0x0101, 2, pcrBase: 3_000),
        };

        using var session = new TsLiveAnalysisSession("test");
        foreach (var packet in packets)
        {
            session.Append(packet);
        }

        var summary = session.Complete();

        Assert.NotNull(summary);
        Assert.Contains("H.264", summary!.Video);
        Assert.Contains("AAC", summary.Audio);
        Assert.True(summary.PidCount >= 2);
        Assert.Equal(0, summary.ErrorCount);
    }

    [Fact]
    public void LiveSession_MapsHevcAndAc3Streams()
    {
        var packets = new List<byte[]>
        {
            CreatePsiPacket(0x0000, 0, BuildPatSection(0x0100)),
            CreatePsiPacket(0x0100, 0, BuildPmtSection(
                0x0101,
                (TsStreamTypes.Hevc, 0x0101),
                (TsStreamTypes.Ac3, 0x0102))),
            CreatePacket(0x0101, 0, pcrBase: 0),
            CreatePesPacket(0x0101, 1, 90_000),
            CreatePesPacket(0x0102, 0, 90_000, 0xBD),
            CreatePacket(0x0101, 2, pcrBase: 3_000),
        };

        using var session = new TsLiveAnalysisSession("test");
        foreach (var packet in packets)
        {
            session.Append(packet);
        }

        var summary = session.Complete();

        Assert.NotNull(summary);
        Assert.Contains("HEVC", summary!.Video);
        Assert.Contains("AC-3", summary.Audio);
    }

    [Fact]
    public void LiveSession_ReportsContinuityGap()
    {
        var packets = new List<byte[]>
        {
            CreatePsiPacket(0x0000, 0, BuildPatSection(0x0100)),
            CreatePsiPacket(0x0100, 0, BuildPmtSection(0x0101, (TsStreamTypes.H264, 0x0101))),
            CreatePacket(0x0101, 0),
            CreatePacket(0x0101, 1),
            CreatePacket(0x0101, 3),
            CreatePacket(0x0101, 4),
        };

        using var session = new TsLiveAnalysisSession("test");
        foreach (var packet in packets)
        {
            session.Append(packet);
        }

        var summary = session.Complete();

        Assert.NotNull(summary);
        Assert.True(summary!.ErrorCount > 0);
        Assert.Equal("error", summary.Verdict);
        Assert.Contains("连续计数缺口", summary.LastEvent);
    }

    private static byte[] CreatePacket(
        int pid,
        int continuityCounter,
        bool transportError = false,
        int adaptationControl = 1,
        long? pcrBase = null)
    {
        var packet = new byte[TsStreamAnalyzer.PacketSize];
        Array.Fill(packet, (byte)0x55);
        packet[0] = 0x47;
        packet[1] = (byte)((transportError ? 0x80 : 0) | ((pid >> 8) & 0x1F));
        packet[2] = (byte)pid;
        packet[3] = (byte)((adaptationControl << 4) | (continuityCounter & 0x0F));
        if (pcrBase is not { } pcr)
        {
            return packet;
        }

        packet[3] = (byte)(0x30 | (continuityCounter & 0x0F));
        packet[4] = 7;
        packet[5] = 0x10;
        packet[6] = (byte)(pcr >> 25);
        packet[7] = (byte)(pcr >> 17);
        packet[8] = (byte)(pcr >> 9);
        packet[9] = (byte)(pcr >> 1);
        packet[10] = (byte)(((pcr & 1) << 7) | 0x7E);
        packet[11] = 0;
        return packet;
    }

    private static byte[] CreatePsiPacket(int pid, int continuityCounter, byte[] section)
    {
        var packet = CreatePacket(pid, continuityCounter);
        packet[1] |= 0x40;
        packet[4] = 0;
        section.CopyTo(packet.AsSpan(5));
        packet.AsSpan(5 + section.Length).Fill(0xFF);
        return packet;
    }

    private static byte[] CreatePesPacket(
        int pid, int continuityCounter, long pts, byte streamId = 0xE0)
    {
        var packet = CreatePacket(pid, continuityCounter);
        packet[1] |= 0x40;
        var payload = packet.AsSpan(4);
        payload[0] = 0;
        payload[1] = 0;
        payload[2] = 1;
        payload[3] = streamId;
        payload[4] = 0;
        payload[5] = 0;
        payload[6] = 0x80;
        payload[7] = 0x80;
        payload[8] = 5;
        WritePts(payload.Slice(9, 5), pts);
        return packet;
    }

    private static byte[] BuildPatSection(int pmtPid) => AppendCrc([
        0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00,
        0x00, 0x01, (byte)(0xE0 | (pmtPid >> 8)), (byte)pmtPid,
    ]);

    private static byte[] BuildPmtSection(
        int pcrPid,
        params (int StreamType, int Pid)[] streams)
    {
        var sectionLength = 9 + streams.Length * 5 + 4;
        var section = new List<byte>
        {
            0x02, (byte)(0xB0 | (sectionLength >> 8)), (byte)sectionLength,
            0x00, 0x01, 0xC1, 0x00, 0x00,
            (byte)(0xE0 | (pcrPid >> 8)), (byte)pcrPid, 0xF0, 0x00,
        };
        foreach (var stream in streams)
        {
            section.Add((byte)stream.StreamType);
            section.Add((byte)(0xE0 | (stream.Pid >> 8)));
            section.Add((byte)stream.Pid);
            section.Add(0xF0);
            section.Add(0x00);
        }

        return AppendCrc(section.ToArray());
    }

    private static byte[] AppendCrc(byte[] section)
    {
        uint crc = uint.MaxValue;
        foreach (var value in section)
        {
            crc = UpdateCrc(crc, value);
        }

        return [.. section, (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= (uint)value << 24;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }

        return crc;
    }

    private static void WritePts(Span<byte> value, long pts)
    {
        value[0] = (byte)(0x21 | (((pts >> 30) & 0x07) << 1));
        value[1] = (byte)(pts >> 22);
        value[2] = (byte)(0x01 | (((pts >> 15) & 0x7F) << 1));
        value[3] = (byte)(pts >> 7);
        value[4] = (byte)(0x01 | ((pts & 0x7F) << 1));
    }
}
