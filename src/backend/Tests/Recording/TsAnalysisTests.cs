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
    public void LiveSession_MapsMultipleAudioTracks()
    {
        var packets = new List<byte[]>
        {
            CreatePsiPacket(0x0000, 0, BuildPatSection(0x0100)),
            CreatePsiPacket(0x0100, 0, BuildPmtSection(
                0x0101,
                (TsStreamTypes.H264, 0x0101),
                (TsStreamTypes.Aac, 0x0102),
                (TsStreamTypes.Ac3, 0x0103))),
            CreatePacket(0x0101, 0, pcrBase: 0),
            CreatePesPacket(0x0101, 1, 90_000),
            CreatePesPacket(0x0102, 0, 90_000, 0xC0),
            CreatePesPacket(0x0103, 0, 90_000, 0xBD),
            CreatePacket(0x0101, 2, pcrBase: 3_000),
        };

        using var session = new TsLiveAnalysisSession("test");
        foreach (var packet in packets)
        {
            session.Append(packet);
        }

        var summary = session.Complete();

        Assert.NotNull(summary);
        Assert.True(summary!.AudioTracks.Length >= 2);
        Assert.Contains(summary.AudioTracks, track => track.Label.Contains("AAC"));
        Assert.Contains(summary.AudioTracks, track => track.Label.Contains("AC-3"));
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
            CreatePacket(0x0101, 0, pcrBase: 0),
            CreatePacket(0x0101, 1, pcrBase: 90_000),
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
        // 事件只带相对录制开始的 0-based 时刻（此处最后已知 PCR 为 1 秒），
        // 绝对时间由前端按浏览器时区渲染，避免服务端时区与用户不一致。
        var gapLine = Assert.Single(summary.AnomalyEvents, line => line.Contains("连续计数缺口"));
        Assert.Contains("00:00:01", gapLine);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}\.\d{3}\*? ", gapLine);
        Assert.NotNull(summary.LastEvent);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}\.\d{3}\*? ", summary.LastEvent);
        Assert.DoesNotContain(" / ", gapLine);
    }

    [Fact]
    public void LiveSession_ShowsOccurrenceCountForMergedSamePidAnomalies()
    {
        var packets = new List<byte[]>
        {
            CreatePsiPacket(0x0000, 0, BuildPatSection(0x0100)),
            CreatePsiPacket(0x0100, 0, BuildPmtSection(0x0101, (TsStreamTypes.H264, 0x0101))),
            CreatePacket(0x0101, 0, pcrBase: 0),
            CreatePacket(0x0101, 1),
            CreatePacket(0x0101, 3),
            CreatePacket(0x0101, 4),
            CreatePacket(0x0101, 6),
        };

        using var session = new TsLiveAnalysisSession("test");
        foreach (var packet in packets)
        {
            session.Append(packet);
        }

        var summary = session.Complete();

        Assert.NotNull(summary);
        // 同一 PID 的两次缺口在合并窗口内应显示发生次数
        var gapLine = Assert.Single(summary!.AnomalyEvents, line => line.Contains("连续计数缺口"));
        Assert.Contains("×2", gapLine);
        Assert.Contains("(PID 0x0101)", gapLine);
        Assert.DoesNotContain("个 PID", gapLine);
    }

    [Fact]
    public void LiveSession_MergesShortBurstAnomaliesAcrossPids()
    {
        var packets = new List<byte[]>
        {
            CreatePsiPacket(0x0000, 0, BuildPatSection(0x0100)),
            CreatePsiPacket(0x0100, 0, BuildPmtSection(
                0x0101,
                (TsStreamTypes.H264, 0x0101),
                (TsStreamTypes.Aac, 0x0102))),
            CreatePacket(0x0101, 0, pcrBase: 0),
            CreatePesPacket(0x0101, 0, 90_000),
            CreatePesPacket(0x0102, 0, 90_000, 0xC0),
            CreatePesPacket(0x0101, 1, 90_000),
            CreatePesPacket(0x0102, 1, 90_000, 0xC0),
            CreatePesPacket(0x0101, 3, 180_000),
            CreatePesPacket(0x0102, 3, 180_000, 0xC0),
        };

        using var session = new TsLiveAnalysisSession("test");
        foreach (var packet in packets)
        {
            session.Append(packet);
        }

        var summary = session.Complete();

        Assert.NotNull(summary);
        // 两个 PID 在相近时刻的同类异常应合并为一条并显示次数
        var gapLine = Assert.Single(summary!.AnomalyEvents, line => line.Contains("连续计数缺口"));
        Assert.Contains("×2", gapLine);
        Assert.Contains("2 个 PID", gapLine);
        // 事件文本只带相对录制开始的时刻，绝对时间由前端按浏览器时区渲染
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}\.\d{3}\*? ", gapLine);
        Assert.DoesNotContain(" / ", gapLine);
    }

    [Fact]
    public void LiveSession_IgnoresAvSyncJitter()
    {
        // 音频按 PES 打包、视频有 B 帧重排，单次采样 PTS 差会成段抖动（一个 GOP 内连续几秒偏一侧）；
        // 这类抖动不能被判成音画漂移。
        var packets = BuildAvStream(
            seconds: 40,
            videoJitter: i => (i % 6) < 3 ? -90_000 : 90_000,
            audioJitter: _ => 0);

        using var session = new TsLiveAnalysisSession("test");
        foreach (var packet in packets)
        {
            session.Append(packet);
        }

        var summary = session.Complete();

        Assert.NotNull(summary);
        Assert.DoesNotContain(summary!.AnomalyEvents, line => line.Contains("音画漂移"));
        Assert.DoesNotContain("音画漂移", summary.LastEvent ?? string.Empty);
    }

    [Fact]
    public void LiveSession_ReportsSustainedAvSyncDriftOnce()
    {
        // 音频整体错位 1.5 秒并持续存在：应上报一次并附带漂移幅度
        var packets = BuildAvStream(
            seconds: 60,
            videoJitter: _ => 0,
            audioJitter: i => i >= 20 ? 135_000 : 0);

        using var session = new TsLiveAnalysisSession("test");
        foreach (var packet in packets)
        {
            session.Append(packet);
        }

        var summary = session.Complete();

        Assert.NotNull(summary);
        var driftLines = summary!.AnomalyEvents.Where(line => line.Contains("音画漂移")).ToArray();
        var driftLine = Assert.Single(driftLines);
        Assert.Contains("+1500ms", driftLine);
    }

    [Fact]
    public void LiveSession_IgnoresContinuityGapsDuringStartupWarmup()
    {
        // 组播录制启动的前几秒（加入组播组、接收预热）会丢初始数据包，
        // 这段缺口单独计数，不计入错误统计；预热之后的缺口照常上报。
        const int videoPid = 0x0101;
        var packets = new List<byte[]>
        {
            CreatePsiPacket(0x0000, 0, BuildPatSection(0x0100)),
            CreatePsiPacket(0x0100, 0, BuildPmtSection(videoPid, (TsStreamTypes.H264, videoPid))),
        };

        var cc = 0;
        for (var step = 0; step < 28; step++)
        {
            if (step == 4)
            {
                cc = (cc + 5) & 0x0F; // 约 1 秒处：预热阶段缺口
            }
            else if (step == 24)
            {
                cc = (cc + 7) & 0x0F; // 约 6 秒处：预热之后缺口
            }

            packets.Add(CreatePacket(videoPid, cc, pcrBase: step * 22_500));
            cc = (cc + 1) & 0x0F;
        }

        using var session = new TsLiveAnalysisSession("test", TsLiveAnalysisSession.StartupWarmupSeconds);
        foreach (var packet in packets)
        {
            session.Append(packet);
        }

        var summary = session.Complete();

        Assert.NotNull(summary);
        Assert.Equal(1, summary!.StartupContinuityGaps);
        Assert.Equal(1, summary.ContinuityErrors);
        var gapLine = Assert.Single(summary.AnomalyEvents, line => line.Contains("连续计数缺口"));
        Assert.Contains("00:00:06", gapLine);
    }

    private static List<byte[]> BuildAvStream(
        int seconds,
        Func<int, long> videoJitter,
        Func<int, long> audioJitter)
    {
        const int videoPid = 0x0101;
        const int audioPid = 0x0102;
        var packets = new List<byte[]>
        {
            CreatePsiPacket(0x0000, 0, BuildPatSection(0x0100)),
            CreatePsiPacket(0x0100, 0, BuildPmtSection(
                videoPid,
                (TsStreamTypes.H264, videoPid),
                (TsStreamTypes.Aac, audioPid))),
        };

        var videoCc = 0;
        var audioCc = 0;
        for (var second = 0; second < seconds; second++)
        {
            var clock = second * 90_000L;
            // 真实码流的 PCR 间隔约几十毫秒，这里每 0.25 秒一个，避免触发 PCR 间隔告警
            for (var step = 0; step < 4; step++)
            {
                packets.Add(CreatePacket(videoPid, videoCc++ & 0x0F, pcrBase: clock + step * 22_500));
            }

            packets.Add(CreatePesPacket(videoPid, videoCc++ & 0x0F, clock + videoJitter(second)));
            packets.Add(CreatePesPacket(audioPid, audioCc++ & 0x0F, clock + audioJitter(second), 0xC0));
        }

        return packets;
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
