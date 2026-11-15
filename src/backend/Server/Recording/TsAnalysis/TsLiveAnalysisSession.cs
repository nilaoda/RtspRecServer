using RtspRecServer.Shared;

namespace RtspRecServer.Server.Recording.TsAnalysis;

internal sealed class TsLiveAnalysisSession : IDisposable
{
    private readonly TsStreamAnalyzer _analyzer = new();
    private readonly TsProgressSink _progressSink = new();
    private long _appendedBytes;
    private bool _completed;

    public TsLiveAnalysisSession(string displayName)
    {
        _analyzer.BeginSession(displayName, new TsStreamAnalyzeOptions
        {
            Features = TsStreamAnalyzeFeatures.ContinuityValidation |
                       TsStreamAnalyzeFeatures.TimestampValidation |
                       TsStreamAnalyzeFeatures.AvSyncValidation |
                       TsStreamAnalyzeFeatures.DetailedEvents |
                       TsStreamAnalyzeFeatures.PesSizeValidation
        }, _progressSink);
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (_completed || data.Length == 0)
        {
            return;
        }

        try
        {
            _analyzer.AppendOutput(data);
            _appendedBytes += data.Length;
        }
        catch
        {
            // 检测失败不能影响录制。
            _completed = true;
        }
    }

    public TsCheckSummary? GetSummary()
    {
        var progress = _progressSink.Latest;
        return progress is null ? null : TsCheckSummaryMapper.FromProgress(progress.Value);
    }

    public TsCheckSummary? Complete()
    {
        if (_completed)
        {
            return GetSummary();
        }

        _completed = true;
        if (_appendedBytes == 0)
        {
            return null;
        }

        try
        {
            return TsCheckSummaryMapper.FromResult(_analyzer.CompleteSession());
        }
        catch
        {
            return GetSummary();
        }
    }

    public void Dispose()
    {
        try
        {
            _analyzer.Dispose();
        }
        catch
        {
            // 忽略关闭检测器时的异常。
        }
    }

    private sealed class TsProgressSink : IProgress<TsCheckProgress>
    {
        private readonly object _lock = new();
        private TsCheckProgress? _latest;

        public TsCheckProgress? Latest
        {
            get
            {
                lock (_lock)
                {
                    return _latest;
                }
            }
        }

        public void Report(TsCheckProgress value)
        {
            lock (_lock)
            {
                _latest = value;
            }
        }
    }
}

internal static class TsCheckSummaryMapper
{
    public static TsCheckSummary FromProgress(TsCheckProgress progress)
    {
        var video = FindPid(progress.Pids, static pid =>
            pid.StreamType is byte streamType && TsStreamTypes.IsVideo(streamType));
        var audio = FindPid(progress.Pids, IsAudio);

        return new TsCheckSummary
        {
            Verdict = progress.ErrorCount > 0
                ? "error"
                : progress.WarningCount > 0
                    ? "warning"
                    : progress.PacketCount > 0
                        ? "pass"
                        : "unknown",
            ErrorCount = progress.ErrorCount,
            WarningCount = progress.WarningCount,
            PacketCount = progress.PacketCount,
            BytesScanned = progress.BytesScanned,
            BitrateKbps = progress.BytesPerSecond * 8 / 1024,
            Video = video is { } videoPid ? FormatPid(videoPid) : null,
            Audio = audio is { } audioPid ? FormatPid(audioPid) : null,
            PidCount = progress.Pids.Count(static pid => pid.Pid >= 0),
            LastEvent = FormatEvent(progress.NewEvent),
        };
    }

    public static TsCheckSummary FromResult(TsCheckResult result)
    {
        var video = FindPid(result.Pids.Values, static pid =>
            pid.StreamType is byte streamType && TsStreamTypes.IsVideo(streamType));
        var audio = FindPid(result.Pids.Values, IsAudio);
        var lastEvent = result.Events.LastOrDefault(static item => item.Severity == TsCheckSeverity.Error)
                        ?? result.Events.LastOrDefault(static item => item.Severity == TsCheckSeverity.Warning);

        return new TsCheckSummary
        {
            Verdict = result.Verdict switch
            {
                TsCheckVerdict.Pass => "pass",
                TsCheckVerdict.Warning => "warning",
                TsCheckVerdict.Error => "error",
                TsCheckVerdict.Incomplete => "incomplete",
                _ => "unknown",
            },
            ErrorCount = result.TotalErrorCount,
            WarningCount = result.TotalWarningCount,
            PacketCount = result.PacketCount,
            BytesScanned = result.BytesScanned,
            BitrateKbps = result.Elapsed.TotalSeconds > 0
                ? result.BytesScanned * 8 / result.Elapsed.TotalSeconds / 1024
                : 0,
            Video = video is { } videoPid ? FormatPid(videoPid) : null,
            Audio = audio is { } audioPid ? FormatPid(audioPid) : null,
            PidCount = result.Pids.Count,
            LastEvent = FormatEvent(lastEvent),
        };
    }

    private static TsCheckPidProgress? FindPid(
        IEnumerable<TsCheckPidProgress> pids,
        Func<TsCheckPidProgress, bool> predicate)
    {
        TsCheckPidProgress? best = null;
        long bestPackets = -1;
        foreach (var pid in pids)
        {
            if (pid.Pid < 0 || !predicate(pid))
            {
                continue;
            }

            if (pid.PacketCount > bestPackets)
            {
                bestPackets = pid.PacketCount;
                best = pid;
            }
        }

        return best;
    }

    private static TsCheckPidSummary? FindPid(
        IEnumerable<TsCheckPidSummary> pids,
        Func<TsCheckPidSummary, bool> predicate)
    {
        TsCheckPidSummary? best = null;
        long bestPackets = -1;
        foreach (var pid in pids)
        {
            if (pid.Pid < 0 || !predicate(pid))
            {
                continue;
            }

            if (pid.PacketCount > bestPackets)
            {
                bestPackets = pid.PacketCount;
                best = pid;
            }
        }

        return best;
    }

    private static bool IsAudio(TsCheckPidProgress pid) =>
        pid.SupplementaryStreamType is TsSupplementaryStreamType.Ac4 or TsSupplementaryStreamType.Opus or
            TsSupplementaryStreamType.Smpte302M or TsSupplementaryStreamType.Dra ||
        pid.StreamType is byte streamType && TsStreamTypes.IsAudio(streamType, pid.SupplementaryStreamType);

    private static bool IsAudio(TsCheckPidSummary pid) =>
        pid.SupplementaryStreamType is TsSupplementaryStreamType.Ac4 or TsSupplementaryStreamType.Opus or
            TsSupplementaryStreamType.Smpte302M or TsSupplementaryStreamType.Dra ||
        pid.StreamType is byte streamType && TsStreamTypes.IsAudio(streamType, pid.SupplementaryStreamType);

    private static string FormatPid(TsCheckPidProgress pid) =>
        FormatStream(pid.StreamType, pid.MpegAudioLayer, pid.SupplementaryStreamType, pid.Language);

    private static string FormatPid(TsCheckPidSummary pid) =>
        FormatStream(pid.StreamType, pid.MpegAudioLayer, pid.SupplementaryStreamType, pid.Language);

    private static string FormatStream(
        byte? streamType,
        TsMpegAudioLayer? mpegAudioLayer,
        TsSupplementaryStreamType? supplementaryStreamType,
        string? language)
    {
        var name = supplementaryStreamType switch
        {
            TsSupplementaryStreamType.Ac4 => "AC-4",
            TsSupplementaryStreamType.Opus => "Opus",
            TsSupplementaryStreamType.Smpte302M => "SMPTE 302M",
            TsSupplementaryStreamType.Dra => "DRA",
            TsSupplementaryStreamType.DvbSubtitle => "DVB 字幕",
            TsSupplementaryStreamType.DvbTeletext => "DVB 图文",
            TsSupplementaryStreamType.AribCaption => "ARIB 字幕",
            TsSupplementaryStreamType.SmpteKlv => "SMPTE KLV",
            TsSupplementaryStreamType.Smpte2038 => "SMPTE 2038",
            TsSupplementaryStreamType.TimedId3 => "ID3",
            _ => mpegAudioLayer switch
            {
                TsMpegAudioLayer.LayerI => "MP1",
                TsMpegAudioLayer.LayerII => "MP2",
                TsMpegAudioLayer.LayerIII => "MP3",
                _ => streamType switch
                {
                    TsStreamTypes.Mpeg1Video => "MPEG-1",
                    TsStreamTypes.Mpeg2Video => "MPEG-2",
                    TsStreamTypes.Mpeg4Video => "MPEG-4 Part 2",
                    TsStreamTypes.H264 => "H.264",
                    TsStreamTypes.Mvc => "MVC",
                    TsStreamTypes.Hevc => "HEVC",
                    TsStreamTypes.Vvc => "VVC",
                    TsStreamTypes.Jpeg2000 => "JPEG 2000",
                    TsStreamTypes.JpegXs => "JPEG XS",
                    TsStreamTypes.Lcevc => "LCEVC",
                    TsStreamTypes.Cavs => "CAVS",
                    TsStreamTypes.Dirac => "Dirac",
                    TsStreamTypes.Avs2 => "AVS2",
                    TsStreamTypes.Avs3 => "AVS3",
                    TsStreamTypes.Vc1 => "VC-1",
                    TsStreamTypes.Mpeg1Audio or TsStreamTypes.Mpeg2Audio => "MPEG Audio",
                    TsStreamTypes.Aac => "AAC",
                    TsStreamTypes.AacLatm => "AAC (LATM)",
                    TsStreamTypes.Mpeg4Audio => "MPEG-4 Audio",
                    TsStreamTypes.Ac3 => "AC-3",
                    TsStreamTypes.Eac3 or TsStreamTypes.Eac3Atsc => "E-AC-3",
                    TsStreamTypes.Eac3Secondary => "E-AC-3 (Secondary)",
                    TsStreamTypes.Dts => "DTS",
                    TsStreamTypes.DtsHd => "DTS-HD",
                    TsStreamTypes.DtsHdMaster => "DTS-HD MA",
                    TsStreamTypes.DtsExpressSecondary => "DTS Express (Secondary)",
                    TsStreamTypes.TrueHd => "TrueHD",
                    TsStreamTypes.BluRayPcm => "LPCM",
                    TsStreamTypes.Av3a => "AV3A",
                    byte value => $"0x{value:X2}",
                    null => "未知",
                },
            },
        };

        return string.IsNullOrEmpty(language) ? name : $"{name} ({language})";
    }

    private static string? FormatEvent(TsCheckEvent? item)
    {
        if (item is null)
        {
            return null;
        }

        var label = item.Type switch
        {
            TsCheckEventType.SyncLoss => "TS 同步丢失",
            TsCheckEventType.TrailingBytes => "尾部残留字节",
            TsCheckEventType.TransportError => "传输错误",
            TsCheckEventType.ContinuityGap => "连续计数缺口",
            TsCheckEventType.PesSizeMismatch => "PES 长度异常",
            TsCheckEventType.DuplicatePacket => "重复包",
            TsCheckEventType.ConflictingDuplicate => "冲突重复包",
            TsCheckEventType.PsiCrcError => "节目表 CRC 错误",
            TsCheckEventType.PsiMalformed => "节目表结构异常",
            TsCheckEventType.PcrBackward => "PCR 倒退",
            TsCheckEventType.PcrJump => "PCR 跳变",
            TsCheckEventType.PcrGap => "PCR 间隔异常",
            TsCheckEventType.PtsBackward => "PTS 倒退",
            TsCheckEventType.PtsJump => "PTS 跳变",
            TsCheckEventType.DtsBackward => "DTS 倒退",
            TsCheckEventType.DtsAfterPts => "DTS 晚于 PTS",
            TsCheckEventType.AvSyncDrift => "音画漂移",
            TsCheckEventType.StreamGap => "音频间隔异常",
            TsCheckEventType.MissingProgramTable => "缺少节目表",
            TsCheckEventType.MissingPcr => "缺少 PCR",
            TsCheckEventType.MissingTimestamp => "缺少时间戳",
            TsCheckEventType.InvalidPacketHeader => "TS 包头异常",
            _ => item.Type.ToString(),
        };

        return item.Pid >= 0 ? $"{label} (PID 0x{item.Pid:X4})" : label;
    }
}
