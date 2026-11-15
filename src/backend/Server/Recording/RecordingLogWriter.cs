using System.Text;
using RtspRecServer.Shared;
using Serilog;

namespace RtspRecServer.Server.Recording;

internal sealed class RecordingLogWriter : IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter _writer;
    private string? _lastVerdict;
    private int _lastErrorCount = -1;
    private int _lastWarningCount = -1;
    private string? _lastEvent;
    private DateTimeOffset? _startedAt;
    private bool _disposed;

    private RecordingLogWriter(string logPath)
    {
        _writer = new StreamWriter(logPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    public static RecordingLogWriter? Create(string outputPath, bool enabled)
    {
        if (!enabled)
        {
            return null;
        }

        try
        {
            var logPath = Path.ChangeExtension(outputPath, ".log");
            var directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return new RecordingLogWriter(logPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "创建录制日志失败: {OutputPath}", outputPath);
            return null;
        }
    }

    public void WriteStart(RecordingTask task, string outputPath, string transport, DateTimeOffset startedAt)
    {
        _startedAt = startedAt;
        var localStart = task.StartTime.ToLocalTime();
        var localEnd = task.EndTime.ToLocalTime();
        var localStartedAt = startedAt.ToLocalTime();
        WriteLine("===== 录制开始 =====");
        WriteLine($"任务: {task.Id} {task.TaskName}");
        WriteLine($"频道: {task.ChannelName} ({task.ChannelId})");
        WriteLine($"类型: {(task.IsLive ? "直播" : "回放")}");
        WriteLine($"计划: {localStart:yyyy-MM-dd HH:mm:ss} -> {localEnd:yyyy-MM-dd HH:mm:ss}");
        WriteLine($"实际开始: {localStartedAt:yyyy-MM-dd HH:mm:ss}");
        WriteLine($"输出: {outputPath}");
        WriteLine($"URL: {task.Url}");
        WriteLine($"传输: {transport}");
        WriteLine("==================");
    }

    public void WriteProgress(RecordingProgress progress)
    {
        var tsCheck = progress.TsCheck;
        if (tsCheck is null || (tsCheck.ErrorCount == 0 && tsCheck.WarningCount == 0))
        {
            return;
        }

        var changed = tsCheck.Verdict != _lastVerdict ||
                      tsCheck.ErrorCount != _lastErrorCount ||
                      tsCheck.WarningCount != _lastWarningCount ||
                      tsCheck.LastEvent != _lastEvent;
        if (!changed)
        {
            return;
        }

        _lastVerdict = tsCheck.Verdict;
        _lastErrorCount = tsCheck.ErrorCount;
        _lastWarningCount = tsCheck.WarningCount;
        _lastEvent = tsCheck.LastEvent;

        var parts = new List<string>
        {
            $"TS={VerdictText(tsCheck.Verdict)}",
            $"包数={tsCheck.PacketCount}",
            $"错误={tsCheck.ErrorCount}",
            $"警告={tsCheck.WarningCount}",
            $"视频={FormatTracks(tsCheck.VideoTracks, tsCheck.Video)}",
            $"音频={FormatTracks(tsCheck.AudioTracks, tsCheck.Audio)}",
            $"PID={tsCheck.PidCount}"
        };
        if (!string.IsNullOrWhiteSpace(tsCheck.LastEvent))
        {
            parts.Add($"最近事件={tsCheck.LastEvent}");
        }

        WriteLine($"[TS] {string.Join(" · ", parts)}");
    }

    public void WriteError(Exception exception)
    {
        WriteLine($"[错误] {exception.GetType().Name}: {exception.Message}");
    }

    public void WriteFinal(
        RecordingStatus status,
        long bytesWritten,
        double? pcrElapsedSeconds,
        string? errorMessage,
        TsCheckSummary? tsCheck)
    {
        var elapsed = _startedAt.HasValue ? DateTimeOffset.Now - _startedAt.Value : (TimeSpan?)null;
        WriteLine("===== 录制结束 =====");
        WriteLine($"状态: {StatusText(status)}");
        WriteLine($"写入: {FormatBytes(bytesWritten)} ({bytesWritten} 字节)");
        if (elapsed.HasValue)
        {
            WriteLine($"耗时: {(int)elapsed.Value.TotalHours:00}:{elapsed.Value.Minutes:00}:{elapsed.Value.Seconds:00}");
        }
        if (pcrElapsedSeconds is > 0)
        {
            WriteLine($"PCR 时长: {pcrElapsedSeconds:F1}s");
        }
        if (tsCheck is not null)
        {
            WriteLine($"TS 检测: {VerdictText(tsCheck.Verdict)} · 错误={tsCheck.ErrorCount} · 警告={tsCheck.WarningCount} · 视频={FormatTracks(tsCheck.VideoTracks, tsCheck.Video)} · 音频={FormatTracks(tsCheck.AudioTracks, tsCheck.Audio)} · PID={tsCheck.PidCount} · 包数={tsCheck.PacketCount}");
            if (!string.IsNullOrWhiteSpace(tsCheck.LastEvent))
            {
                WriteLine($"最近事件: {tsCheck.LastEvent}");
            }
        }
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            WriteLine($"错误: {errorMessage}");
        }
        WriteLine("==================");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Dispose();
        }
    }

    private void WriteLine(string message)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _writer.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "写入录制日志失败");
                _disposed = true;
                try
                {
                    _writer.Dispose();
                }
                catch
                {
                    // 忽略关闭失败。
                }
            }
        }
    }

    private static string VerdictText(string verdict) => verdict switch
    {
        "pass" => "正常",
        "warning" => "警告",
        "error" => "异常",
        "incomplete" => "未完成",
        _ => "未知",
    };

    private static string FormatTracks(IReadOnlyList<TsStreamTrack> tracks, string? fallback)
    {
        var labels = tracks
            .Select(track => track.Label)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return labels.Length > 0 ? string.Join(" / ", labels) : fallback ?? "未识别";
    }

    private static string StatusText(RecordingStatus status) => status switch
    {
        RecordingStatus.Pending => "等待中",
        RecordingStatus.Recording => "录制中",
        RecordingStatus.Completed => "已完成",
        RecordingStatus.Failed => "失败",
        RecordingStatus.Stopped => "已停止",
        _ => status.ToString(),
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        string[] units = ["KB", "MB", "GB", "TB"];
        var value = bytes / 1024d;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:F2} {units[unit]}";
    }
}
