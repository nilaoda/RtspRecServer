using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

/// <summary>
/// 定时规则的持久化存储（timers.json），模式与 RecordingTaskStore 一致：
/// 内存字典 + 脏标记定期落盘 + tmp/rename 原子替换。
/// </summary>
public sealed class RecordingTimerStore : IDisposable
{
    private readonly ConcurrentDictionary<long, RecordingTimer> _timers = new();
    private readonly object _fileLock = new();
    private readonly object _stateLock = new();
    private readonly string _timersPath;
    private readonly ILogger<RecordingTimerStore> _logger;
    private long _idSeed;
    private bool _isDirty;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _saveTask;

    public RecordingTimerStore(ILogger<RecordingTimerStore> logger)
        : this(logger, Path.Combine(AppContext.BaseDirectory, "timers.json"))
    {
    }

    internal RecordingTimerStore(ILogger<RecordingTimerStore> logger, string timersPath)
    {
        _logger = logger;
        _timersPath = timersPath;
        LoadFromDisk();
        _saveTask = Task.Run(PeriodicSaveAsync);
    }

    public IReadOnlyCollection<RecordingTimer> GetAll()
    {
        return _timers.Values.ToArray();
    }

    public RecordingTimer? GetById(long id)
    {
        return _timers.TryGetValue(id, out var timer) ? timer : null;
    }

    public RecordingTimer Add(RecordingTimer timer)
    {
        lock (_stateLock)
        {
            var id = Interlocked.Increment(ref _idSeed);
            var created = timer with { Id = id };
            _timers[created.Id] = created;
            _isDirty = true;
            return created;
        }
    }

    public RecordingTimer? TryUpdate(long id, Func<RecordingTimer, RecordingTimer?> update)
    {
        lock (_stateLock)
        {
            if (!_timers.TryGetValue(id, out var current))
            {
                return null;
            }

            var updated = update(current);
            if (updated is null)
            {
                return null;
            }

            _timers[id] = updated;
            _isDirty = true;
            return updated;
        }
    }

    public bool Remove(long id)
    {
        lock (_stateLock)
        {
            var removed = _timers.TryRemove(id, out _);
            if (removed)
            {
                _isDirty = true;
            }

            return removed;
        }
    }

    private async Task PeriodicSaveAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), _cts.Token);
                if (_isDirty)
                {
                    PersistSnapshot();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "定时规则定时保存失败");
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            if (_isDirty)
            {
                PersistSnapshot();
            }
            _saveTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 忽略关闭时的异常
        }
        _cts.Dispose();
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(_timersPath))
        {
            return;
        }

        lock (_fileLock)
        {
            try
            {
                var json = File.ReadAllText(_timersPath);
                var timers = JsonSerializer.Deserialize(json, RtspJsonContext.Default.ListRecordingTimer) ?? new List<RecordingTimer>();
                foreach (var timer in timers)
                {
                    _timers[timer.Id] = timer;
                }

                if (timers.Count > 0)
                {
                    _idSeed = timers.Max(t => t.Id);
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "无法加载定时规则文件 {TimersPath}", _timersPath);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "定时规则文件 {TimersPath} 格式错误", _timersPath);
            }
        }
    }

    private void PersistSnapshot()
    {
        lock (_fileLock)
        {
            _isDirty = false;
            try
            {
                var snapshot = _timers.Values.OrderBy(t => t.Id).ToList();
                var json = JsonSerializer.Serialize(snapshot, RtspJsonContext.Default.ListRecordingTimer);
                var tempPath = _timersPath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _timersPath, true);
            }
            catch (IOException ex)
            {
                _isDirty = true;
                _logger.LogWarning(ex, "无法保存定时规则文件 {TimersPath}", _timersPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _isDirty = true;
                _logger.LogWarning(ex, "没有权限保存定时规则文件 {TimersPath}", _timersPath);
            }
        }
    }
}
