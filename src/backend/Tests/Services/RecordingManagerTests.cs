using Microsoft.Extensions.Logging.Abstractions;
using RtspRecServer.Server.Recording;
using RtspRecServer.Server.Services;
using RtspRecServer.Shared;

namespace RtspRecServer.Tests.Services;

public class RecordingManagerTests : IDisposable
{
    private readonly string _tasksPath;
    private readonly RecordingTaskStore _store;
    private readonly RecordingManager _manager;
    private readonly RecordingNotifierFake _notifier = new();

    public RecordingManagerTests()
    {
        _tasksPath = Path.Combine(Path.GetTempPath(), $"rtsp-tasks-{Guid.NewGuid():N}.json");
        _store = new RecordingTaskStore(NullLogger<RecordingTaskStore>.Instance, _tasksPath);
        _manager = new RecordingManager(
            _store,
            new ConfigurationService(),
            new NoopRecordingService(),
            _notifier,
            new TaskErrorLogService(_notifier),
            NullLogger<RecordingManager>.Instance);
    }

    [Fact]
    public async Task UpdateTaskEndTime_UpdatesPendingLiveTask()
    {
        var start = DateTimeOffset.UtcNow.AddHours(1);
        var task = AddTask(isLive: true, start, start.AddHours(1));
        var newEnd = start.AddHours(2);

        var updated = await _manager.UpdateTaskEndTimeAsync(task.Id, newEnd, CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(newEnd, updated!.EndTime);
        Assert.Equal(newEnd, _store.GetById(task.Id)!.EndTime);
        Assert.Single(_notifier.Tasks);
    }

    [Fact]
    public async Task UpdateTaskEndTime_RejectsPlaybackTask()
    {
        var start = DateTimeOffset.UtcNow.AddHours(1);
        var task = AddTask(isLive: false, start, start.AddHours(1));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.UpdateTaskEndTimeAsync(task.Id, start.AddHours(2), CancellationToken.None));

        Assert.Contains("仅直播任务", exception.Message);
    }

    [Fact]
    public async Task UpdateTaskEndTime_RejectsPastEndTime()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        var task = AddTask(isLive: true, start, start.AddHours(1));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.UpdateTaskEndTimeAsync(task.Id, DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None));

        Assert.Contains("晚于当前时间", exception.Message);
    }

    [Fact]
    public async Task UpdateTaskEndTime_RejectsEndTimeBeforeStart()
    {
        var start = DateTimeOffset.UtcNow.AddHours(1);
        var task = AddTask(isLive: true, start, start.AddHours(1));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.UpdateTaskEndTimeAsync(task.Id, start.AddMinutes(-1), CancellationToken.None));

        Assert.Contains("大于开始时间", exception.Message);
    }

    [Fact]
    public async Task ReconcileInterruptedTasks_MarksRecordingTasksFailed()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var task = AddTask(isLive: true, start, start.AddHours(1), RecordingStatus.Recording);

        await _manager.ReconcileInterruptedTasksAsync(CancellationToken.None);

        var stored = _store.GetById(task.Id);
        Assert.NotNull(stored);
        Assert.Equal(RecordingStatus.Failed, stored!.Status);
        Assert.Equal("服务重启中断", stored.ErrorMessage);
        Assert.Single(_notifier.Tasks);
    }

    [Fact]
    public async Task UpdateTaskEndTime_RejectsCompletedLiveTask()
    {
        var start = DateTimeOffset.UtcNow.AddHours(1);
        var task = AddTask(isLive: true, start, start.AddHours(1));
        _store.TryUpdate(task.Id, current => current with { Status = RecordingStatus.Completed });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.UpdateTaskEndTimeAsync(task.Id, start.AddHours(2), CancellationToken.None));

        Assert.Contains("仅等待中或录制中", exception.Message);
        Assert.Equal(RecordingStatus.Completed, _store.GetById(task.Id)!.Status);
    }

    [Fact]
    public async Task FailMissedTask_DoesNotFailTaskThatWasExtended()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        var task = AddTask(isLive: true, start, start.AddHours(1));
        var staleTask = _store.GetById(task.Id)!;
        var extendedEnd = DateTimeOffset.UtcNow.AddHours(1);
        _store.TryUpdate(task.Id, current => current with { EndTime = extendedEnd });

        await _manager.FailMissedTaskAsync(staleTask, CancellationToken.None);

        var stored = _store.GetById(task.Id)!;
        Assert.Equal(RecordingStatus.Pending, stored.Status);
        Assert.Equal(extendedEnd, stored.EndTime);
        Assert.Empty(_notifier.Tasks);
    }

    [Fact]
    public void DeleteTasks_DeletesExistingTasksAndReportsMissingIds()
    {
        var start = DateTimeOffset.UtcNow.AddHours(1);
        var first = AddTask(isLive: true, start, start.AddHours(1));
        var second = AddTask(isLive: true, start.AddHours(2), start.AddHours(3));

        var result = _manager.DeleteTasks([first.Id, second.Id, 9999]);

        Assert.Equal(2, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Equal([first.Id, second.Id], result.SucceededIds);
        Assert.Equal([9999], result.FailedIds);
        Assert.Empty(_store.GetAll());
    }

    [Fact]
    public async Task StopTasks_ReportsNonRecordingTasksAsFailed()
    {
        var start = DateTimeOffset.UtcNow.AddHours(1);
        var task = AddTask(isLive: true, start, start.AddHours(1));

        var result = await _manager.StopTasksAsync([task.Id, 9999], CancellationToken.None);

        Assert.Equal(0, result.Succeeded);
        Assert.Equal(2, result.Failed);
        Assert.Equal([task.Id, 9999], result.FailedIds);
    }

    [Fact]
    public async Task StopTasks_StopsActiveRecordingTasks()
    {
        var tasksPath = Path.Combine(Path.GetTempPath(), $"rtsp-bulk-stop-{Guid.NewGuid():N}.json");
        using var store = new RecordingTaskStore(NullLogger<RecordingTaskStore>.Instance, tasksPath);
        var service = new BlockingRecordingService();
        var manager = new RecordingManager(
            store,
            new ConfigurationService(),
            service,
            _notifier,
            new TaskErrorLogService(_notifier),
            NullLogger<RecordingManager>.Instance);
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var task = store.Add(new RecordingTask
        {
            ChannelId = 1,
            ChannelName = "CCTV-1",
            Url = "rtp://239.1.1.1:8000",
            TaskName = "bulk-stop",
            DisplayName = "bulk-stop",
            IsLive = true,
            StartTime = start,
            EndTime = start.AddHours(1)
        });

        try
        {
            Assert.True(await manager.TryStartTaskAsync(task, CancellationToken.None));
            await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var result = await manager.StopTasksAsync([task.Id], CancellationToken.None);

            Assert.Equal(1, result.Succeeded);
            Assert.Equal(0, result.Failed);
            Assert.Equal([task.Id], result.SucceededIds);
            Assert.Equal(RecordingStatus.Stopped, store.GetById(task.Id)!.Status);
        }
        finally
        {
            if (File.Exists(tasksPath))
            {
                File.Delete(tasksPath);
            }
        }
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_tasksPath))
        {
            File.Delete(_tasksPath);
        }
    }

    private RecordingTask AddTask(
        bool isLive,
        DateTimeOffset start,
        DateTimeOffset end,
        RecordingStatus status = RecordingStatus.Pending)
    {
        return _store.Add(new RecordingTask
        {
            ChannelId = 1,
            ChannelName = "CCTV-1",
            Url = isLive ? "http://udpxy/rtp/239.1.1.1:8000" : "rtsp://host/PLTV/1.smil",
            TaskName = "task",
            DisplayName = "task",
            IsLive = isLive,
            StartTime = start,
            EndTime = end,
            Status = status
        });
    }

    private sealed class RecordingNotifierFake : IRecordingNotifier
    {
        public List<RecordingTaskDto> Tasks { get; } = new();
        public List<TaskErrorLogMessage> ErrorLogMessages { get; } = new();

        public Task NotifyTaskUpdateAsync(RecordingTaskDto task, CancellationToken cancellationToken)
        {
            Tasks.Add(task);
            return Task.CompletedTask;
        }

        public Task NotifyTaskErrorLogAsync(TaskErrorLogMessage message, CancellationToken cancellationToken)
        {
            ErrorLogMessages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingRecordingService : IRecordingService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RecordingResult> RecordAsync(
            RecordingTask task,
            string outputPath,
            TimeSpan? targetDuration,
            string recordingTransport,
            Action<RecordingProgress> onProgress,
            CancellationToken cancellationToken,
            Stream? output = null)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            return new RecordingResult
            {
                Success = true,
                OutputPath = outputPath
            };
        }
    }

    private sealed class NoopRecordingService : IRecordingService
    {
        public Task<RecordingResult> RecordAsync(
            RecordingTask task,
            string outputPath,
            TimeSpan? targetDuration,
            string recordingTransport,
            Action<RecordingProgress> onProgress,
            CancellationToken cancellationToken,
            Stream? output = null)
        {
            return Task.FromResult(new RecordingResult
            {
                Success = true,
                OutputPath = outputPath
            });
        }
    }
}
