using RtspRecServer.Shared;

namespace RtspRecServer.Server.Recording.TsAnalysis;

internal sealed class TsLiveAnalysisSession : IDisposable
{
    private readonly TsStreamAnalyzer _analyzer = new();
    private readonly TsProgressSink _progressSink = new();
    private long _appendedBytes;
    private DateTimeOffset? _wallClockStart;
    private bool _completed;

    public TsLiveAnalysisSession(string displayName)
    {
        _analyzer.BeginSession(displayName, new TsStreamAnalyzeOptions
        {
            ProgressIntervalMilliseconds = 500,
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

        // 首个数据到达的墙钟时间，用于把 0-based 事件时间换算成本地时间
        _wallClockStart ??= DateTimeOffset.Now;
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
        return progress is null
            ? null
            : TsCheckSummaryMapper.FromProgress(progress.Value, _progressSink.AnomalyEvents, _wallClockStart);
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
            return TsCheckSummaryMapper.FromResult(_analyzer.CompleteSession(), _wallClockStart);
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
        private readonly List<TsCheckEvent> _anomalyEvents = [];

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

        public IReadOnlyList<TsCheckEvent> AnomalyEvents
        {
            get
            {
                lock (_lock)
                {
                    return _anomalyEvents.ToArray();
                }
            }
        }

        public void Report(TsCheckProgress value)
        {
            lock (_lock)
            {
                _latest = value;
                if (value.NewEvent is { } newEvent &&
                    newEvent.Severity != TsCheckSeverity.Info &&
                    _anomalyEvents.Count < TsCheckSummaryMapper.MaxTrackedAnomalyEvents)
                {
                    _anomalyEvents.Add(newEvent);
                }
            }
        }
    }
}

// 码率按 SI 换算：1 kbps = 1000 bit/s，与 ffmpeg/MediaInfo 一致；文件大小仍按 1024 进制。
internal static class TsCheckSummaryMapper
{
    public const int MaxAnomalyEvents = 5;
    public const int MaxTrackedAnomalyEvents = 50;
    private const double AnomalyGroupWindowSeconds = 3.0;

    public static TsCheckSummary FromProgress(
        TsCheckProgress progress,
        IReadOnlyList<TsCheckEvent>? anomalyEvents = null,
        DateTimeOffset? wallClockStart = null)
    {
        var videoTracks = FindTracks(
            progress.Pids,
            static pid => pid.Pid >= 0 && pid.StreamType is byte streamType && TsStreamTypes.IsVideo(streamType),
            static pid => pid.Pid,
            static pid => pid.PacketCount,
            static pid => FormatPid(pid));
        var audioTracks = FindTracks(
            progress.Pids,
            static pid => pid.Pid >= 0 && IsAudio(pid),
            static pid => pid.Pid,
            static pid => pid.PacketCount,
            static pid => FormatPid(pid));

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
            ContinuityErrors = progress.ContinuityErrors,
            PacketCount = progress.PacketCount,
            BytesScanned = progress.BytesScanned,
            BitrateKbps = progress.BytesPerSecond * 8 / 1000,
            Video = videoTracks.FirstOrDefault()?.Label,
            Audio = audioTracks.FirstOrDefault()?.Label,
            VideoTracks = videoTracks,
            AudioTracks = audioTracks,
            PidCount = progress.Pids.Count(static pid => pid.Pid >= 0),
            // 没有新事件的进度也会保留最近一条异常，避免“最近事件”在两帧之间消失
            LastEvent = progress.NewEvent is { } newEvent
                ? FormatEvent(newEvent, wallClockStart, newEvent.Occurrences)
                : anomalyEvents is { Count: > 0 } tracked
                    ? FormatEvent(tracked[tracked.Count - 1], wallClockStart, tracked[tracked.Count - 1].Occurrences)
                    : null,
            AnomalyEvents = BuildAnomalyEvents(anomalyEvents ?? [], wallClockStart),
        };
    }

    public static TsCheckSummary FromResult(TsCheckResult result, DateTimeOffset? wallClockStart = null)
    {
        var videoTracks = FindTracks(
            result.Pids.Values,
            static pid => pid.Pid >= 0 && pid.StreamType is byte streamType && TsStreamTypes.IsVideo(streamType),
            static pid => pid.Pid,
            static pid => pid.PacketCount,
            static pid => FormatPid(pid));
        var audioTracks = FindTracks(
            result.Pids.Values,
            static pid => pid.Pid >= 0 && IsAudio(pid),
            static pid => pid.Pid,
            static pid => pid.PacketCount,
            static pid => FormatPid(pid));
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
            ContinuityErrors = result.Pids.Values.Sum(static pid => pid.ContinuityErrors),
            PacketCount = result.PacketCount,
            BytesScanned = result.BytesScanned,
            BitrateKbps = result.Elapsed.TotalSeconds > 0
                ? result.BytesScanned * 8 / result.Elapsed.TotalSeconds / 1000
                : 0,
            Video = videoTracks.FirstOrDefault()?.Label,
            Audio = audioTracks.FirstOrDefault()?.Label,
            VideoTracks = videoTracks,
            AudioTracks = audioTracks,
            PidCount = result.Pids.Count,
            LastEvent = lastEvent is null ? null : FormatEvent(lastEvent, wallClockStart, lastEvent.Occurrences),
            AnomalyEvents = BuildAnomalyEvents(result.Events, wallClockStart),
        };
    }

    private static string[] BuildAnomalyEvents(IEnumerable<TsCheckEvent> events, DateTimeOffset? wallClockStart)
    {
        // 同级别、同类型且在短时间内的异常合并为一条（跨 PID 也合并），避免短时间大量异常刷屏
        var groups = new List<AnomalyGroup>(MaxAnomalyEvents);
        var lastGroupByKey = new Dictionary<(TsCheckSeverity, TsCheckEventType), AnomalyGroup>();
        foreach (var item in events)
        {
            if (item.Severity == TsCheckSeverity.Info)
            {
                continue;
            }

            var key = (item.Severity, item.Type);
            AnomalyGroup? group = null;
            if (lastGroupByKey.TryGetValue(key, out var candidate))
            {
                var withinWindow = item.TimeSeconds is not { } time ||
                                   candidate.LastTime is not { } lastTime ||
                                   time - lastTime <= AnomalyGroupWindowSeconds;
                if (withinWindow)
                {
                    group = candidate;
                }
            }

            if (group is null)
            {
                group = new AnomalyGroup
                {
                    Severity = item.Severity,
                    Type = item.Type,
                    First = item
                };
                groups.Add(group);
            }

            group.Occurrences += item.Occurrences;
            group.Pids.Add(item.Pid);
            if (item.TimeSeconds is { } eventTime)
            {
                group.LastTime = eventTime;
            }

            lastGroupByKey[key] = group;
        }

        return groups
            .Take(MaxAnomalyEvents)
            .Select(group => FormatEvent(group.First, wallClockStart, group.Occurrences, group.Pids.Count))
            .ToArray();
    }

    private sealed class AnomalyGroup
    {
        public required TsCheckSeverity Severity { get; init; }
        public required TsCheckEventType Type { get; init; }
        public required TsCheckEvent First { get; init; }
        public double? LastTime { get; set; }
        public long Occurrences { get; set; }
        public HashSet<int> Pids { get; } = [];
    }

    private static TsStreamTrack[] FindTracks<T>(
        IEnumerable<T> pids,
        Func<T, bool> predicate,
        Func<T, int> getPid,
        Func<T, long> getPacketCount,
        Func<T, string> format)
    {
        return pids
            .Where(predicate)
            .OrderByDescending(getPacketCount)
            .Select(pid => new TsStreamTrack
            {
                Pid = getPid(pid),
                Label = format(pid)
            })
            .ToArray();
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

    public static string FormatEvent(
        TsCheckEvent item,
        DateTimeOffset? wallClockStart = null,
        long occurrences = 1,
        int pidCount = 1)
    {
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

        var pidText = pidCount > 1
            ? $"({pidCount} 个 PID)"
            : item.Pid >= 0
                ? $"(PID 0x{item.Pid:X4})"
                : string.Empty;
        var text = pidText.Length > 0 ? $"{label} {pidText}" : label;

        string? timeText = null;
        if (item.TimeSeconds is { } seconds)
        {
            var relative = $"{TsCheckEvent.FormatTime(seconds)}{(item.IsEstimatedTime ? "*" : string.Empty)}";
            timeText = wallClockStart is { } start
                ? $"{start.ToLocalTime().AddSeconds(seconds):HH:mm:ss} / {relative}"
                : relative;
        }

        var countText = occurrences > 1 ? $"×{occurrences}" : null;
        return string.Join(' ', new[] { timeText, countText, text }.Where(part => !string.IsNullOrEmpty(part)));
    }
}
