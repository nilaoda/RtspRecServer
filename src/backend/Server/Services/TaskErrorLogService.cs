using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

/// <summary>
/// 录制任务的实时错误日志：进程内环形缓冲（关闭页面不丢，服务重启清空），通过 WebSocket 推送增量。
/// </summary>
public sealed class TaskErrorLogService
{
    public const int DefaultMaxEntries = 10_000;
    public const int MinMaxEntries = 100;
    public const int MaxMaxEntries = 50_000;
    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxEntriesCacheDuration = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private readonly List<TaskErrorLogEntry> _entries = [];
    private readonly IRecordingNotifier _notifier;
    private readonly Func<int> _maxEntriesProvider;
    private readonly Func<DateTimeOffset> _clock;
    private int _cachedMaxEntries = DefaultMaxEntries;
    private DateTimeOffset _maxEntriesReadAt = DateTimeOffset.MinValue;
    private long _nextId;
    private long _nextRevision;
    private long _generation;

    public TaskErrorLogService(IRecordingNotifier notifier)
        : this(notifier, () => DefaultMaxEntries, null)
    {
    }

    public TaskErrorLogService(IRecordingNotifier notifier, ConfigurationService configurationService)
        : this(notifier, () => configurationService.GetAppConfig().TaskErrorLogMaxEntries, null)
    {
    }

    internal TaskErrorLogService(IRecordingNotifier notifier, Func<DateTimeOffset>? clock)
        : this(notifier, () => DefaultMaxEntries, clock)
    {
    }

    internal TaskErrorLogService(IRecordingNotifier notifier, Func<int> maxEntriesProvider, Func<DateTimeOffset>? clock)
    {
        _notifier = notifier;
        _maxEntriesProvider = maxEntriesProvider;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public TaskErrorLogSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new TaskErrorLogSnapshot
            {
                Generation = _generation,
                Entries = _entries.ToArray()
            };
        }
    }

    public TaskErrorLogMessage Clear()
    {
        TaskErrorLogMessage message;
        lock (_sync)
        {
            _entries.Clear();
            _generation++;
            message = new TaskErrorLogMessage
            {
                Generation = _generation,
                Reset = true,
                Entries = []
            };
        }

        _ = _notifier.NotifyTaskErrorLogAsync(message, CancellationToken.None);
        return message;
    }

    public void RecordTaskChange(RecordingTask? previous, RecordingTask current)
    {
        var currentCheck = current.TsCheck;
        if (currentCheck?.LastEvent is { Length: > 0 } lastEvent &&
            !string.Equals(lastEvent, previous?.TsCheck?.LastEvent, StringComparison.Ordinal))
        {
            var errorDelta = currentCheck.ErrorCount - (previous?.TsCheck?.ErrorCount ?? 0);
            var warningDelta = currentCheck.WarningCount - (previous?.TsCheck?.WarningCount ?? 0);
            if (previous?.TsCheck is null || errorDelta > 0 || warningDelta > 0)
            {
                AppendOrMerge(current, errorDelta > 0 ? "error" : "warning", lastEvent);
            }
        }

        if (current.Status == RecordingStatus.Failed &&
            !string.IsNullOrWhiteSpace(current.ErrorMessage) &&
            (previous is null ||
             previous.Status != RecordingStatus.Failed ||
             !string.Equals(previous.ErrorMessage, current.ErrorMessage, StringComparison.Ordinal)))
        {
            AppendOrMerge(current, "error", $"失败：{current.ErrorMessage}");
        }
    }

    private void AppendOrMerge(RecordingTask task, string severity, string message)
    {
        TaskErrorLogMessage? outbound;
        lock (_sync)
        {
            var now = _clock();
            var identity = ExtractIdentity(message);
            var taskName = string.IsNullOrWhiteSpace(task.DisplayName) ? task.TaskName : task.DisplayName;
            var fileName = string.IsNullOrWhiteSpace(task.FilePath) ? null : Path.GetFileName(task.FilePath);

            var last = FindLastForTask(task.Id);
            if (last is not null &&
                string.Equals(last.Identity, identity, StringComparison.Ordinal) &&
                now - last.Timestamp <= MergeWindow)
            {
                var updated = last with
                {
                    Revision = ++_nextRevision,
                    Timestamp = now,
                    Message = message,
                    Severity = severity == "error" ? "error" : last.Severity,
                    TaskName = taskName,
                    ChannelName = task.ChannelName,
                    FileName = fileName ?? last.FileName
                };
                var index = _entries.FindIndex(item => item.Id == last.Id);
                if (index >= 0)
                {
                    _entries[index] = updated;
                }

                outbound = new TaskErrorLogMessage
                {
                    Generation = _generation,
                    Entries = [updated]
                };
            }
            else
            {
                var entry = new TaskErrorLogEntry
                {
                    Id = ++_nextId,
                    Revision = ++_nextRevision,
                    TaskId = task.Id,
                    TaskName = taskName,
                    ChannelName = task.ChannelName,
                    FileName = fileName,
                    Severity = severity,
                    Message = message,
                    Identity = identity,
                    Timestamp = now
                };
                _entries.Add(entry);
                var maxEntries = GetMaxEntries();
                if (_entries.Count > maxEntries)
                {
                    _entries.RemoveRange(0, _entries.Count - maxEntries);
                }

                outbound = new TaskErrorLogMessage
                {
                    Generation = _generation,
                    Entries = [entry]
                };
            }
        }

        _ = _notifier.NotifyTaskErrorLogAsync(outbound, CancellationToken.None);
    }

    private int GetMaxEntries()
    {
        // 配置变更后最多 5 秒内生效，避免每条异常都读一次配置文件
        var now = _clock();
        if (now - _maxEntriesReadAt < MaxEntriesCacheDuration)
        {
            return _cachedMaxEntries;
        }

        _cachedMaxEntries = Math.Clamp(_maxEntriesProvider(), MinMaxEntries, MaxMaxEntries);
        _maxEntriesReadAt = now;
        return _cachedMaxEntries;
    }

    private TaskErrorLogEntry? FindLastForTask(long taskId)
    {
        for (var index = _entries.Count - 1; index >= 0; index--)
        {
            if (_entries[index].TaskId == taskId)
            {
                return _entries[index];
            }
        }
        return null;
    }

    internal static string ExtractIdentity(string message)
    {
        var text = message;
        // 去掉 "本地时间 / " 前缀（HH:mm:ss / ）
        if (text.Length > 11 &&
            char.IsDigit(text[0]) && char.IsDigit(text[1]) && text[2] == ':' &&
            char.IsDigit(text[3]) && char.IsDigit(text[4]) && text[5] == ':' &&
            char.IsDigit(text[6]) && char.IsDigit(text[7]) &&
            text[8] == ' ' && text[9] == '/' && text[10] == ' ')
        {
            text = text[11..];
        }

        // 去掉相对时刻 "HH:MM:SS.mmm*" 与可选的 "×N "
        if (text.Length >= 12 &&
            char.IsDigit(text[0]) && char.IsDigit(text[1]) && text[2] == ':' &&
            char.IsDigit(text[3]) && char.IsDigit(text[4]) && text[5] == ':' &&
            char.IsDigit(text[6]) && char.IsDigit(text[7]) && text[8] == '.' &&
            char.IsDigit(text[9]) && char.IsDigit(text[10]) && char.IsDigit(text[11]))
        {
            var index = 12;
            if (index < text.Length && text[index] == '*')
            {
                index++;
            }
            if (index < text.Length && text[index] == ' ')
            {
                index++;
            }

            var rest = text[index..];
            if (rest.StartsWith('×'))
            {
                var space = rest.IndexOf(' ');
                if (space > 1)
                {
                    rest = rest[(space + 1)..];
                }
            }

            return rest.Trim();
        }

        return text.Trim();
    }
}