using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

public sealed class RecordingTaskStore : IDisposable
{
    private readonly ConcurrentDictionary<long, RecordingTask> _tasks = new();
    private readonly object _fileLock = new();
    private readonly string _tasksPath;
    private readonly ILogger<RecordingTaskStore> _logger;
    private long _idSeed;
    private bool _isDirty;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _saveTask;

    public RecordingTaskStore(ILogger<RecordingTaskStore> logger)
    {
        _logger = logger;
        _tasksPath = Path.Combine(AppContext.BaseDirectory, "tasks.json");
        LoadFromDisk();
        _saveTask = Task.Run(PeriodicSaveAsync);
    }

    public IReadOnlyCollection<RecordingTask> GetAll()
    {
        return _tasks.Values.ToArray();
    }

    public RecordingTask? GetById(long id)
    {
        return _tasks.TryGetValue(id, out var task) ? task : null;
    }

    public RecordingTask Add(RecordingTask task)
    {
        var id = Interlocked.Increment(ref _idSeed);
        var created = task with { Id = id };
        _tasks[created.Id] = created;
        _isDirty = true;
        return created;
    }

    public RecordingTask Update(RecordingTask task)
    {
        _tasks[task.Id] = task;
        _isDirty = true;
        return task;
    }

    public bool Remove(long id)
    {
        var removed = _tasks.TryRemove(id, out _);
        if (removed)
        {
            _isDirty = true;
        }

        return removed;
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
                _logger.LogWarning(ex, "定时保存任务失败");
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
        if (!File.Exists(_tasksPath))
        {
            return;
        }

        lock (_fileLock)
        {
            try
            {
                var json = File.ReadAllText(_tasksPath);
                var tasks = JsonSerializer.Deserialize(json, RtspJsonContext.Default.ListRecordingTask) ?? new List<RecordingTask>();
                foreach (var task in tasks)
                {
                    _tasks[task.Id] = task;
                }

                if (tasks.Count > 0)
                {
                    _idSeed = tasks.Max(t => t.Id);
                }
            }
            catch (IOException ex)
            {
                // 文件访问冲突或权限问题，记录错误但不中断应用程序
                _logger.LogWarning(ex, "无法加载任务文件 {TasksPath}", _tasksPath);
            }
            catch (JsonException ex)
            {
                // JSON 格式错误，记录错误但不中断应用程序
                _logger.LogWarning(ex, "任务文件 {TasksPath} 格式错误", _tasksPath);
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
                var snapshot = _tasks.Values.OrderBy(t => t.Id).ToList();
                var json = JsonSerializer.Serialize(snapshot, RtspJsonContext.Default.ListRecordingTask);
                var tempPath = _tasksPath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _tasksPath, true);
            }
            catch (IOException ex)
            {
                _isDirty = true;
                // 文件访问冲突、权限问题或磁盘空间不足，记录错误但不中断应用程序
                _logger.LogWarning(ex, "无法保存任务文件 {TasksPath}", _tasksPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _isDirty = true;
                // 权限不足，记录错误但不中断应用程序
                _logger.LogWarning(ex, "没有权限访问任务文件 {TasksPath}", _tasksPath);
            }
        }
    }
}
