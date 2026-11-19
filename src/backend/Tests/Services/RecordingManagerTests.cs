using System.Collections.Concurrent;
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
        var directory = Directory.CreateTempSubdirectory("rtsp-bulk-stop-");
        var configuration = new ConfigurationService(directory.FullName);
        configuration.UpdateAppConfig(new AppConfigUpdateRequest { RecordPath = directory.FullName });
        var service = new BlockingRecordingService(pauseOnStop: true);
        var manager = new RecordingManager(
            _store,
            configuration,
            service,
            _notifier,
            new TaskErrorLogService(_notifier),
            NullLogger<RecordingManager>.Instance);
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var task = _store.Add(new RecordingTask
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

        Task<RecordingTaskBulkResult>? stopping = null;
        try
        {
            Assert.True(await manager.TryStartTaskAsync(task, CancellationToken.None));
            await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            stopping = manager.StopTasksAsync([task.Id], CancellationToken.None);
            await service.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(manager.IsActive(task.Id));
            Assert.Contains(Path.Combine(directory.FullName, "bulk-stop.ts"), manager.GetActiveOutputPaths());
            Assert.False(manager.DeleteTask(task.Id));

            service.FinishStopping.TrySetResult();
            var result = await stopping.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, result.Succeeded);
            Assert.Equal(0, result.Failed);
            Assert.Equal([task.Id], result.SucceededIds);
            Assert.Equal(RecordingStatus.Stopped, _store.GetById(task.Id)!.Status);
            Assert.False(manager.IsActive(task.Id));
        }
        finally
        {
            service.FinishStopping.TrySetResult();
            if (stopping is not null)
            {
                await stopping.WaitAsync(TimeSpan.FromSeconds(2));
            }
            else
            {
                await manager.StopTaskAsync(task.Id, CancellationToken.None);
            }
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task StartTask_UsesDifferentFilesForExistingAndActiveRecordings()
    {
        var directory = Directory.CreateTempSubdirectory("rtsp-recording-name-");
        var config = new ConfigurationService(directory.FullName);
        config.UpdateAppConfig(new AppConfigUpdateRequest { RecordPath = directory.FullName, WriteRecordingLog = false });
        var manager = new RecordingManager(_store, config, new BlockingRecordingService(), _notifier,
            new TaskErrorLogService(_notifier), NullLogger<RecordingManager>.Instance);
        var start = DateTimeOffset.UtcNow;
        var task = new RecordingTask { TaskName = "CON.同名/节目:4K", Url = "rtp://239.1.1.1:8000", IsLive = true, StartTime = start, EndTime = start.AddHours(1) };
        var first = _store.Add(task);
        var second = _store.Add(task with { EndTime = start.AddHours(2) });
        var originalPath = Path.Combine(directory.FullName, "_CON.同名_节目_4K.ts");

        try
        {
            await File.WriteAllTextAsync(originalPath, "已有录像");
            Assert.True(await manager.TryStartTaskAsync(first, CancellationToken.None));
            Assert.True(await manager.TryStartTaskAsync(second, CancellationToken.None));

            Assert.Equal(Path.Combine(directory.FullName, "_CON.同名_节目_4K_2.ts"), _store.GetById(first.Id)!.FilePath);
            Assert.Equal(Path.Combine(directory.FullName, "_CON.同名_节目_4K_3.ts"), _store.GetById(second.Id)!.FilePath);
            Assert.Equal("已有录像", await File.ReadAllTextAsync(originalPath));
        }
        finally
        {
            await manager.StopTasksAsync([first.Id, second.Id], CancellationToken.None);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task StartTask_CountsSharedLiveSourcesAndWritersSeparately()
    {
        var directory = Directory.CreateTempSubdirectory("rtsp-source-capacity-");
        var configuration = new ConfigurationService(directory.FullName);
        configuration.UpdateAppConfig(new AppConfigUpdateRequest { RecordPath = directory.FullName, WriteRecordingLog = false });
        var manager = new RecordingManager(_store, configuration, new BlockingRecordingService(), _notifier,
            new TaskErrorLogService(_notifier), NullLogger<RecordingManager>.Instance);
        var now = DateTimeOffset.UtcNow;
        var task = new RecordingTask
        {
            TaskName = "A", Url = "rtp://239.1.1.1:8000?localaddr=192.0.2.1",
            IsLive = true, StartTime = now, EndTime = now.AddHours(1)
        };
        var first = _store.Add(task);
        var equivalent = _store.Add(task with { TaskName = "B", Url = "rtp://@239.1.1.1:8000/?localAddress=192.0.2.1" });
        var third = _store.Add(task with { TaskName = "C" });
        var otherInterface = _store.Add(task with { TaskName = "D", Url = "rtp://239.1.1.1:8000?localaddr=192.0.2.2" });

        try
        {
            Assert.True(await manager.TryStartTaskAsync(first, CancellationToken.None));
            Assert.False(await manager.TryStartTaskAsync(otherInterface, CancellationToken.None));
            var reasons = manager.GetTaskRuntime(now).PendingReasons;
            Assert.Equal("sourceLimit", reasons[otherInterface.Id]);
            Assert.Equal("dispatch", reasons[equivalent.Id]);
            Assert.True(await manager.TryStartTaskAsync(equivalent, CancellationToken.None));
            Assert.False(await manager.TryStartTaskAsync(third, CancellationToken.None));
            var runtime = manager.GetTaskRuntime(now);
            Assert.Equal("writerLimit", runtime.PendingReasons[third.Id]);
            Assert.Equal(1, runtime.ActiveSourceCount);
            Assert.Equal(1, runtime.SourceLimit);
            Assert.Equal(2, runtime.ActiveWriterCount);
            Assert.Equal(2, runtime.WriterLimit);
            Assert.Equal(2, manager.ActiveCount);

            Assert.True(await manager.StopTaskAsync(first.Id, CancellationToken.None));
            Assert.False(await manager.TryStartTaskAsync(otherInterface, CancellationToken.None));
            Assert.True(await manager.TryStartTaskAsync(third, CancellationToken.None));
        }
        finally
        {
            await manager.StopTasksAsync([first.Id, equivalent.Id, third.Id, otherInterface.Id], CancellationToken.None);
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("rtsp://example.test/source", false)]
    [InlineData("rtp://239.1.1.1:8000", true)]
    [InlineData("rtsp://example.test/source", true)]
    public async Task RetryTask_PreservesOriginalRecording(string url, bool expiredLive)
    {
        var directory = Directory.CreateTempSubdirectory("rtsp-retry-");
        try
        {
            var config = new ConfigurationService(directory.FullName);
            var channel = config.AddChannel(new ChannelConfig { Name = "频道", Url = url });
            var manager = new RecordingManager(_store, config, new NoopRecordingService(), _notifier,
                new TaskErrorLogService(_notifier), NullLogger<RecordingManager>.Instance);
            var start = DateTimeOffset.UtcNow.AddHours(-2);
            var original = _store.Add(new RecordingTask
            {
                ChannelId = channel.Id, DisplayName = "测试录像", StartTime = start, EndTime = start.AddHours(1),
                IsLive = expiredLive, Status = RecordingStatus.Failed, ErrorMessage = "网络中断", FilePath = "old.ts"
            });

            if (expiredLive)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RetryTaskAsync(original.Id, CancellationToken.None));
                Assert.Contains("请选择新的录制时间", error.Message);
                Assert.Single(_store.GetAll());
            }
            else
            {
                var retry = await manager.RetryTaskAsync(original.Id, CancellationToken.None);
                Assert.NotNull(retry);
                Assert.NotEqual(original.Id, retry.Id);
                Assert.Equal(original.StartTime, retry.StartTime);
                Assert.Equal(original.EndTime, retry.EndTime);
                Assert.Equal(RecordingStatus.Pending, retry.Status);
                await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RetryTaskAsync(original.Id, CancellationToken.None));
                Assert.Equal(2, _store.GetAll().Count);
            }

            Assert.Equal(original, _store.GetById(original.Id));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CreateTask_RejectsEndTimeAtStartTime()
    {
        var start = DateTimeOffset.UtcNow;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.CreateTaskAsync(
            new RecordingTaskCreateRequest { StartTime = start, EndTime = start }, CancellationToken.None));

        Assert.Contains("结束时间必须大于开始时间", error.Message);
        Assert.Empty(_store.GetAll());
    }

    [Fact]
    public async Task TimerScan_SkipsOnlyWindowsFullyCoveredByAnotherTask()
    {
        var directory = Directory.CreateTempSubdirectory("rtsp-timer-overlap-");
        try
        {
            var config = new ConfigurationService(directory.FullName);
            var channel = config.AddChannel(new ChannelConfig { Name = "Test", Url = "udp://239.1.1.1:8000" });
            var manager = new RecordingManager(_store, config, new NoopRecordingService(), _notifier,
                new TaskErrorLogService(_notifier), NullLogger<RecordingManager>.Instance);
            using var timers = new RecordingTimerStore(NullLogger<RecordingTimerStore>.Instance, Path.Combine(directory.FullName, "timers.json"));
            var service = new RecordingTimerService(timers, config, manager, NullLogger<RecordingTimerService>.Instance);
            var first = timers.Add(new RecordingTimer
            {
                ChannelId = channel.Id,
                Name = "First",
                StartTime = "10:00",
                EndTime = "11:00",
                StartDate = "2026-11-19",
                EndDate = "2026-11-19",
                Weekdays = [true, true, true, true, true, true, true]
            });
            timers.Add(first with { Name = "Covered", StartTime = "10:10", EndTime = "10:40" });
            timers.Add(first with { Name = "Overlap", StartTime = "10:30", EndTime = "12:00" });
            var localNow = new DateTime(2026, 11, 19, 9, 0, 0);
            var now = new DateTimeOffset(localNow, TimeZoneInfo.Local.GetUtcOffset(localNow));

            await service.ScanAndGenerateAsync(now, CancellationToken.None);

            Assert.Equal(["First", "Overlap"], manager.GetAllTasks().Select(task => task.DisplayName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkipTimerTask_KeepsFutureOccurrence(bool legacyTask)
    {
        var directory = Directory.CreateTempSubdirectory("rtsp-timer-skip-");
        try
        {
            var config = new ConfigurationService(directory.FullName);
            var channel = config.AddChannel(new ChannelConfig { Name = "Test", Url = "udp://239.1.1.1:8000" });
            var manager = new RecordingManager(_store, config, new NoopRecordingService(), _notifier,
                new TaskErrorLogService(_notifier), NullLogger<RecordingManager>.Instance);
            using var timers = new RecordingTimerStore(NullLogger<RecordingTimerStore>.Instance, Path.Combine(directory.FullName, "timers.json"));
            var service = new RecordingTimerService(timers, config, manager, NullLogger<RecordingTimerService>.Instance);
            var timer = timers.Add(new RecordingTimer
            {
                ChannelId = channel.Id, Name = "每日录制", StartTime = "10:00", EndTime = "11:00",
                StartDate = "2026-11-19", EndDate = "2026-11-20", Weekdays = [true, true, true, true, true, true, true]
            });
            var localNow = new DateTime(2026, 11, 19, 9, 0, 0);
            var now = new DateTimeOffset(localNow, TimeZoneInfo.Local.GetUtcOffset(localNow));
            await service.ScanAndGenerateAsync(now, CancellationToken.None);
            var created = manager.GetAllTasks().OrderBy(task => task.StartTime).ToArray();
            Assert.Equal(2, created.Length);
            if (legacyTask)
            {
                _store.TryUpdate(created[0].Id, task => task with { TimerWindowStart = null });
            }

            var skipped = await manager.SkipTimerTaskAsync(created[0].Id, CancellationToken.None);
            Assert.NotNull(skipped);
            Assert.True(skipped.IsSkipped);
            Assert.Contains(_notifier.Tasks, task => task.Id == skipped.Id && task.IsSkipped && task.Status == RecordingStatus.Stopped);

            timers.TryUpdate(timer.Id, current => current with { LastTriggeredAt = null });
            await service.ScanAndGenerateAsync(now, CancellationToken.None);
            Assert.Equal(2, manager.GetAllTasks().Count);
            Assert.Equal(RecordingStatus.Pending, manager.GetTask(created[1].Id)!.Status);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExtendLiveTasks_UsesEachCurrentEndTimeAndSkipsPendingTasks()
    {
        var directory = Directory.CreateTempSubdirectory("rtsp-bulk-extend-");
        var config = new ConfigurationService(directory.FullName);
        config.UpdateAppConfig(new AppConfigUpdateRequest { RecordPath = directory.FullName, WriteRecordingLog = false });
        var manager = new RecordingManager(_store, config, new BlockingRecordingService(), _notifier,
            new TaskErrorLogService(_notifier), NullLogger<RecordingManager>.Instance);
        var now = DateTimeOffset.UtcNow;
        var first = AddTask(true, now, now.AddMinutes(30));
        var second = AddTask(true, now, now.AddMinutes(45));
        var pending = AddTask(true, now.AddMinutes(10), now.AddHours(1));
        try
        {
            Assert.True(await manager.TryStartTaskAsync(first, CancellationToken.None));
            Assert.True(await manager.TryStartTaskAsync(second, CancellationToken.None));
            await manager.UpdateTaskEndTimeAsync(first.Id, now.AddMinutes(35), CancellationToken.None);

            var result = await manager.ExtendLiveTasksAsync([first.Id, second.Id, first.Id, pending.Id], 15, CancellationToken.None);

            Assert.Equal([first.Id, second.Id], result.SucceededIds);
            Assert.Equal([pending.Id], result.FailedIds);
            Assert.Equal(now.AddMinutes(50), manager.GetTask(first.Id)!.EndTime);
            Assert.Equal(now.AddMinutes(60), manager.GetTask(second.Id)!.EndTime);
            Assert.Equal(pending.EndTime, manager.GetTask(pending.Id)!.EndTime);
        }
        finally
        {
            await manager.StopTasksAsync([first.Id, second.Id], CancellationToken.None);
            directory.Delete(recursive: true);
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
        public ConcurrentQueue<RecordingTaskDto> Tasks { get; } = new();
        public ConcurrentQueue<TaskErrorLogMessage> ErrorLogMessages { get; } = new();

        public Task NotifyTaskUpdateAsync(RecordingTaskDto task, CancellationToken cancellationToken)
        {
            Tasks.Enqueue(task);
            return Task.CompletedTask;
        }

        public Task NotifyTaskErrorLogAsync(TaskErrorLogMessage message, CancellationToken cancellationToken)
        {
            ErrorLogMessages.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingRecordingService(bool pauseOnStop = false) : IRecordingService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishStopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
                Stopping.TrySetResult();
                if (pauseOnStop)
                {
                    await FinishStopping.Task;
                }
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
