using System.Collections.Concurrent;
using System.Text.Json;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

public sealed class RecordingTaskStore
{
    private readonly ConcurrentDictionary<long, RecordingTask> _tasks = new();
    private readonly object _fileLock = new();
    private readonly string _tasksPath;
    private long _idSeed;

    public RecordingTaskStore()
    {
        _tasksPath = Path.Combine(AppContext.BaseDirectory, "tasks.json");
        LoadFromDisk();
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
        PersistSnapshot();
        return created;
    }

    public RecordingTask Update(RecordingTask task)
    {
        _tasks[task.Id] = task;
        PersistSnapshot();
        return task;
    }

    public bool Remove(long id)
    {
        var removed = _tasks.TryRemove(id, out _);
        if (removed)
        {
            PersistSnapshot();
        }

        return removed;
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(_tasksPath))
        {
            return;
        }

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

    private void PersistSnapshot()
    {
        lock (_fileLock)
        {
            var snapshot = _tasks.Values.OrderBy(t => t.Id).ToList();
            var json = JsonSerializer.Serialize(snapshot, RtspJsonContext.Default.ListRecordingTask);
            var tempPath = _tasksPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _tasksPath, true);
        }
    }
}
