using System.Collections.Concurrent;
using System.IO;
using RtspRecServer.Server.Recording;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

public sealed class RecordingManager
{
    private readonly RecordingTaskStore _taskStore;
    private readonly ConfigurationService _configurationService;
    private readonly IRecordingService _recordingService;
    private readonly IRecordingNotifier _notifier;
    private readonly ILogger<RecordingManager> _logger;
    private readonly ConcurrentDictionary<long, RecordingSession> _activeSessions = new();
    private readonly object _taskTransitionLock = new();

    public RecordingManager(
        RecordingTaskStore taskStore,
        ConfigurationService configurationService,
        IRecordingService recordingService,
        IRecordingNotifier notifier,
        ILogger<RecordingManager> logger)
    {
        _taskStore = taskStore;
        _configurationService = configurationService;
        _recordingService = recordingService;
        _notifier = notifier;
        _logger = logger;
    }

    public int ActiveCount => _activeSessions.Count;

    public IReadOnlyCollection<RecordingTask> GetAllTasks()
    {
        var tasks = _taskStore.GetAll().OrderBy(t => t.Id).ToList();
        var mergedTasks = new List<RecordingTask>(tasks.Count);
        
        foreach (var task in tasks)
        {
            if (_activeSessions.TryGetValue(task.Id, out var session))
            {
                mergedTasks.Add(task with 
                { 
                    BytesWritten = session.BytesWritten,
                    PcrElapsedSeconds = session.GetPcrElapsedSeconds()
                });
            }
            else
            {
                mergedTasks.Add(task);
            }
        }
        
        return mergedTasks;
    }

    public RecordingTask? GetTask(long id)
    {
        var task = _taskStore.GetById(id);
        if (task != null && _activeSessions.TryGetValue(id, out var session))
        {
            return task with 
            { 
                BytesWritten = session.BytesWritten,
                PcrElapsedSeconds = session.GetPcrElapsedSeconds()
            };
        }
        return task;
    }

    public async Task<RecordingTask> CreateTaskAsync(RecordingTaskCreateRequest request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("开始创建录制任务，频道ID: {ChannelId}, 开始时间: {StartTime}, 结束时间: {EndTime}", 
            request.ChannelId, request.StartTime, request.EndTime);

        var channels = _configurationService.GetChannels();
        var channel = channels.FirstOrDefault(c => c.Id == request.ChannelId);
        if (channel == null)
        {
            _logger.LogError("创建录制任务失败：频道不存在，频道ID: {ChannelId}", request.ChannelId);
            throw new InvalidOperationException("频道不存在");
        }

        // 检查是否存在相同参数的任务
        var existingTasks = _taskStore.GetAll()
            .Where(t => t.ChannelId == request.ChannelId && 
                       t.StartTime == request.StartTime && 
                       t.EndTime == request.EndTime)
            .ToList();

        if (existingTasks.Any())
        {
            _logger.LogWarning("创建录制任务被拒绝：存在相同参数的任务，频道ID: {ChannelId}, 开始时间: {StartTime}, 结束时间: {EndTime}", 
                request.ChannelId, request.StartTime, request.EndTime);
            throw new InvalidOperationException("已存在相同参数的录制任务");
        }

        var isLive = LiveSourceClassifier.IsLive(channel.Url);

        var localStart = request.StartTime.ToLocalTime();
        var suffix = isLive
            ? $"{channel.Name}_{localStart:yyyyMMddHHmmss}"
            : $"{channel.Name}_{localStart:yyyyMMddHHmmss}_{request.EndTime.ToLocalTime():yyyyMMddHHmmss}";
        var baseName = string.IsNullOrWhiteSpace(request.TaskName)
            ? (isLive ? "Live" : "Playback")
            : request.TaskName.Trim();
        var taskName = $"{baseName}_{suffix}";

        var task = new RecordingTask
        {
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            Url = channel.Url,
            TaskName = taskName,
            DisplayName = baseName,
            IsLive = isLive,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            Status = RecordingStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            TimerId = request.TimerId
        };

        var prepared = task with { Url = isLive ? task.Url : RecordingUrlBuilder.Build(task) };
        var created = _taskStore.Add(prepared);
        _logger.LogInformation("录制任务创建成功，任务ID: {TaskId}, 任务名称: {TaskName}, 频道: {ChannelName}", 
            created.Id, created.TaskName, created.ChannelName);
        
        await _notifier.NotifyTaskUpdateAsync(ToDto(created), cancellationToken);
        return created;
    }

    public bool DeleteTask(long id)
    {
        lock (_taskTransitionLock)
        {
            if (_activeSessions.ContainsKey(id))
            {
                return false;
            }

            return _taskStore.Remove(id);
        }
    }

    /// <summary>
    /// 删除定时规则预生成的未开始任务（规则更新/删除后由 RecordingTimerService 调用）。
    /// </summary>
    public int RemovePendingTasksByTimer(long timerId)
    {
        var removed = 0;
        foreach (var task in _taskStore.GetAll().Where(t => t.TimerId == timerId && t.Status == RecordingStatus.Pending))
        {
            if (DeleteTask(task.Id))
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("已清理定时规则 {TimerId} 预生成的 {Count} 个待录任务", timerId, removed);
        }
        return removed;
    }

    public async Task<bool> StopTaskAsync(long id, CancellationToken cancellationToken)
    {
        _logger.LogInformation("尝试停止录制任务，任务ID: {TaskId}", id);

        RecordingSession? session = null;
        lock (_taskTransitionLock)
        {
            if (_activeSessions.TryRemove(id, out session))
            {
                session.ManualStopRequested = true;
                session.Cancellation.Cancel();
            }
        }

        if (session is not null)
        {
            await session.Completion;
            _logger.LogInformation("录制任务停止成功，任务ID: {TaskId}", id);
            return true;
        }

        _logger.LogWarning("停止录制任务失败：任务未在运行中，任务ID: {TaskId}", id);
        return false;
    }

    public async Task<RecordingTaskBulkResult> StopTasksAsync(
        IReadOnlyCollection<long> taskIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var distinctIds = taskIds.Where(id => id > 0).Distinct().ToArray();
        var sessions = new List<(long Id, RecordingSession Session)>();

        lock (_taskTransitionLock)
        {
            foreach (var id in distinctIds)
            {
                if (_activeSessions.TryRemove(id, out var session))
                {
                    session.ManualStopRequested = true;
                    session.Cancellation.Cancel();
                    sessions.Add((id, session));
                }
            }
        }

        if (sessions.Count > 0)
        {
            await Task.WhenAll(sessions.Select(item => item.Session.Completion));
        }

        var succeededIds = sessions.Select(item => item.Id).ToArray();
        var succeededSet = succeededIds.ToHashSet();
        var failedIds = distinctIds.Where(id => !succeededSet.Contains(id)).ToArray();
        _logger.LogInformation(
            "批量停止任务完成：成功 {Succeeded}，失败 {Failed}",
            succeededIds.Length,
            failedIds.Length);

        return new RecordingTaskBulkResult
        {
            Succeeded = succeededIds.Length,
            Failed = failedIds.Length,
            SucceededIds = succeededIds,
            FailedIds = failedIds
        };
    }

    public RecordingTaskBulkResult DeleteTasks(IReadOnlyCollection<long> taskIds)
    {
        var distinctIds = taskIds.Where(id => id > 0).Distinct().ToArray();
        var succeededIds = new List<long>();
        var failedIds = new List<long>();
        foreach (var id in distinctIds)
        {
            if (DeleteTask(id))
            {
                succeededIds.Add(id);
            }
            else
            {
                failedIds.Add(id);
            }
        }

        _logger.LogInformation(
            "批量删除任务记录完成：成功 {Succeeded}，失败 {Failed}",
            succeededIds.Count,
            failedIds.Count);

        return new RecordingTaskBulkResult
        {
            Succeeded = succeededIds.Count,
            Failed = failedIds.Count,
            SucceededIds = succeededIds.ToArray(),
            FailedIds = failedIds.ToArray()
        };
    }

    public async Task<RecordingTask?> UpdateTaskEndTimeAsync(long id, DateTimeOffset endTime, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        RecordingTask? updated;
        RecordingSession? session = null;

        lock (_taskTransitionLock)
        {
            if (_activeSessions.TryGetValue(id, out session) && session.ScheduledStopRequested)
            {
                throw new InvalidOperationException("任务已到结束时间并开始停止，无法延长");
            }

            updated = _taskStore.TryUpdate(id, current =>
            {
                if (!current.IsLive)
                {
                    throw new InvalidOperationException("仅直播任务支持修改结束时间");
                }

                if (current.Status is not (RecordingStatus.Pending or RecordingStatus.Recording))
                {
                    throw new InvalidOperationException("仅等待中或录制中的任务可修改结束时间");
                }

                if (endTime <= current.StartTime)
                {
                    throw new InvalidOperationException("结束时间必须大于开始时间");
                }

                if (endTime <= now)
                {
                    throw new InvalidOperationException("结束时间必须晚于当前时间");
                }

                return current with { EndTime = endTime };
            });

            if (updated is null)
            {
                return null;
            }

            session?.UpdateEndTime(endTime);
        }

        var result = session is null
            ? updated
            : updated with
            {
                BytesWritten = session.BytesWritten,
                PcrElapsedSeconds = session.GetPcrElapsedSeconds()
            };
        var dto = ToDto(result) with
        {
            CurrentBitrateKbps = session?.GetCurrentBitrateKbps()
        };
        await _notifier.NotifyTaskUpdateAsync(dto, cancellationToken);
        _logger.LogInformation("录制任务结束时间已更新，任务ID: {TaskId}, 新结束时间: {EndTime}", id, endTime);
        return result;
    }

    public async Task FailMissedTaskAsync(RecordingTask task, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var failed = _taskStore.TryUpdate(task.Id, current =>
            current.Status == RecordingStatus.Pending && current.EndTime <= now
                ? current with
                {
                    Status = RecordingStatus.Failed,
                    ErrorMessage = "已超过计划结束时间，未启动",
                    FinishedAt = now
                }
                : null);

        if (failed is null)
        {
            return;
        }

        _logger.LogWarning("直播任务已错过结束时间，标记失败，任务ID: {TaskId}", task.Id);
        await _notifier.NotifyTaskUpdateAsync(ToDto(failed), cancellationToken);
    }

    public void StopSessionsDue(DateTimeOffset now)
    {
        lock (_taskTransitionLock)
        {
            foreach (var session in _activeSessions.Values)
            {
                if (session.IsDue(now))
                {
                    session.RequestScheduledStop();
                }
            }
        }
    }

    public async Task ReconcileInterruptedTasksAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var interruptedTasks = _taskStore.GetAll()
            .Where(task => task.Status == RecordingStatus.Recording)
            .ToList();

        foreach (var task in interruptedTasks)
        {
            if (_activeSessions.ContainsKey(task.Id))
            {
                continue;
            }

            var failed = _taskStore.TryUpdate(task.Id, current =>
                current.Status == RecordingStatus.Recording
                    ? current with
                    {
                        Status = RecordingStatus.Failed,
                        ErrorMessage = "服务重启中断",
                        FinishedAt = now
                    }
                    : null);

            if (failed is null)
            {
                continue;
            }

            _logger.LogWarning("录制任务因服务重启中断，任务ID: {TaskId}", failed.Id);
            await _notifier.NotifyTaskUpdateAsync(ToDto(failed), cancellationToken);
        }
    }

    public async Task<bool> TryStartTaskAsync(RecordingTask task, CancellationToken cancellationToken)
    {
        _logger.LogInformation("尝试启动录制任务，任务ID: {TaskId}, 任务名称: {TaskName}, 频道: {ChannelName}",
            task.Id, task.TaskName, task.ChannelName);

        var config = _configurationService.GetAppConfig();
        RecordingTask started;
        RecordingSession session;
        string outputPath;
        var sessionCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_taskTransitionLock)
        {
            var currentTask = _taskStore.GetById(task.Id);
            if (currentTask is null || currentTask.Status != RecordingStatus.Pending)
            {
                _logger.LogWarning("录制任务启动失败：任务不存在或已不在等待状态，任务ID: {TaskId}", task.Id);
                return false;
            }

            if (_activeSessions.ContainsKey(task.Id))
            {
                _logger.LogWarning("录制任务启动失败：任务已在运行中，任务ID: {TaskId}", task.Id);
                return false;
            }

            var recordPath = ResolveRecordPath(config.RecordPath);
            var fileName = currentTask.TaskName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                ? currentTask.TaskName
                : $"{currentTask.TaskName}.ts";
            outputPath = Path.Combine(recordPath, fileName);

            _logger.LogInformation("录制任务输出路径: {OutputPath}", outputPath);

            var startedAt = DateTimeOffset.UtcNow;
            var updated = _taskStore.TryUpdate(task.Id, current =>
                current.Status == RecordingStatus.Pending
                    ? current with
                    {
                        Status = RecordingStatus.Recording,
                        StartedAt = startedAt,
                        FilePath = outputPath
                    }
                    : null);

            if (updated is null)
            {
                _logger.LogWarning("录制任务启动失败：任务状态已变化，任务ID: {TaskId}", task.Id);
                return false;
            }

            started = updated;
            session = new RecordingSession(started.Id, outputPath, started.IsLive, started.EndTime);
            session.Completion = sessionCompletion.Task;
            if (!_activeSessions.TryAdd(started.Id, session))
            {
                _taskStore.TryUpdate(started.Id, current => current with
                {
                    Status = RecordingStatus.Failed,
                    ErrorMessage = "任务已在运行中",
                    FinishedAt = DateTimeOffset.UtcNow
                });
                _logger.LogWarning("录制任务启动失败：任务已在运行中，任务ID: {TaskId}", task.Id);
                return false;
            }
        }

        // 回放 URL 已在创建任务时构造；直播任务直接使用原始地址。
        try
        {
            await _notifier.NotifyTaskUpdateAsync(ToDto(started), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "发送录制任务启动通知失败，任务ID: {TaskId}", started.Id);
        }

        var duration = !started.IsLive && started.EndTime > started.StartTime
            ? started.EndTime - started.StartTime
            : (TimeSpan?)null;

        _ = Task.Run(async () =>
        {
            var lastUpdate = DateTimeOffset.UtcNow;
            var lastBytes = 0L;
            using var recordingLog = RecordingLogWriter.Create(outputPath, config.WriteRecordingLog);
            recordingLog?.WriteStart(
                started,
                outputPath,
                LiveSourceClassifier.GetTransportDescription(started.Url, config.RecordingTransport),
                started.StartedAt ?? DateTimeOffset.Now);
            try
            {
                var result = await _recordingService.RecordAsync(started, outputPath, duration, config.RecordingTransport, (progress) =>
                {
                    session.BytesWritten = progress.BytesWritten;
                    session.UpdateBitrate(progress.BytesWritten); // 更新实时码率
                    session.UpdatePcrElapsedSeconds(progress.PcrElapsedSeconds); // 更新PCR时长
                    recordingLog?.WriteProgress(progress);

                    if (DateTimeOffset.UtcNow - lastUpdate > TimeSpan.FromSeconds(1) && progress.BytesWritten != lastBytes)
                    {
                        lastUpdate = DateTimeOffset.UtcNow;
                        lastBytes = progress.BytesWritten;
                        var currentBitrate = session.GetCurrentBitrateKbps();
                        var pcrElapsed = session.GetPcrElapsedSeconds();
                        var update = _taskStore.TryUpdate(started.Id, current => current with
                        {
                            BytesWritten = progress.BytesWritten,
                            TsCheck = progress.TsCheck ?? current.TsCheck
                        });

                        if (update is not null)
                        {
                            _ = _notifier.NotifyTaskUpdateAsync(
                                ToDto(update) with { CurrentBitrateKbps = currentBitrate, PcrElapsedSeconds = pcrElapsed },
                                CancellationToken.None);
                        }
                    }
                }, session.Cancellation.Token);

                var finalStatus = session.Cancellation.IsCancellationRequested
                    ? session.ManualStopRequested
                        ? RecordingStatus.Stopped
                        : RecordingStatus.Completed
                    : result.Success
                        ? RecordingStatus.Completed
                        : RecordingStatus.Failed;

                // 增加 PCR 时长校验逻辑：如果录制状态不是手动停止或已失败，则检查 PCR 时长
                var finalErrorMessage = result.ErrorMessage;
                if (finalStatus == RecordingStatus.Completed && !started.IsLive)
                {
                    var plannedDurationSeconds = (started.EndTime - started.StartTime).TotalSeconds;
                    var actualPcrSeconds = result.PcrElapsedSeconds ?? 0;

                    // 如果实际录制的 PCR 时长比预估时长少超过 60 秒（1 分钟），则标记为失败
                    if (plannedDurationSeconds > 60 && actualPcrSeconds < (plannedDurationSeconds - 60))
                    {
                        finalStatus = RecordingStatus.Failed;
                        finalErrorMessage = $"录制时长不足：预估 {plannedDurationSeconds:F1}s，实际 PCR {actualPcrSeconds:F1}s (差距 > 60s)";
                        _logger.LogWarning("任务 {TaskId} 录制时长不足，判定为失败。预估: {Planned}s, 实际: {Actual}s", 
                            started.Id, plannedDurationSeconds, actualPcrSeconds);
                    }
                }

                var fileBytes = 0L;
                if (File.Exists(outputPath))
                {
                    fileBytes = new FileInfo(outputPath).Length;
                }
                var finalBytesWritten = Math.Max(result.BytesWritten, fileBytes);

                _logger.LogInformation("录制任务完成，任务ID: {TaskId}, 状态: {Status}, 写入字节数: {BytesWritten}", 
                    started.Id, finalStatus, finalBytesWritten);

                if (finalStatus == RecordingStatus.Failed)
                {
                    _logger.LogError("录制任务 {TaskId} 失败。频道: {ChannelName}, URL: {Url}, 输出路径: {OutputPath}, 具体原因: {ErrorMessage}", 
                        started.Id, started.ChannelName, started.Url, outputPath, finalErrorMessage ?? "未知错误");
                }

                var currentBitrate = session.GetCurrentBitrateKbps();
                var completed = _taskStore.TryUpdate(started.Id, current => current with
                {
                    Status = finalStatus,
                    BytesWritten = finalBytesWritten,
                    ErrorMessage = finalErrorMessage ?? (finalStatus == RecordingStatus.Failed ? "录制异常停止" : null),
                    FinishedAt = DateTimeOffset.UtcNow,
                    PcrElapsedSeconds = result.PcrElapsedSeconds,
                    TsCheck = result.TsCheck ?? current.TsCheck
                });

                if (completed is not null)
                {
                    await _notifier.NotifyTaskUpdateAsync(
                        ToDto(completed) with { CurrentBitrateKbps = currentBitrate, PcrElapsedSeconds = result.PcrElapsedSeconds },
                        CancellationToken.None);
                }

                recordingLog?.WriteFinal(
                    finalStatus,
                    finalBytesWritten,
                    result.PcrElapsedSeconds,
                    finalErrorMessage,
                    completed?.TsCheck ?? result.TsCheck);
            }
            catch (Exception ex)
            {
                recordingLog?.WriteError(ex);
                _logger.LogError(ex, "录制任务发生严重异常 {TaskId}。频道: {ChannelName}, URL: {Url}, 错误堆栈: {StackTrace}", 
                    started.Id, started.ChannelName, started.Url, ex.StackTrace);
                var currentBitrate = session.GetCurrentBitrateKbps();
                var failed = _taskStore.TryUpdate(started.Id, current => current with
                {
                    Status = RecordingStatus.Failed,
                    ErrorMessage = $"{ex.Message} (Stack: {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()})",
                    FinishedAt = DateTimeOffset.UtcNow
                });

                var failedFileBytes = File.Exists(outputPath) ? new FileInfo(outputPath).Length : session.BytesWritten;
                recordingLog?.WriteFinal(RecordingStatus.Failed, failedFileBytes, null, ex.Message, failed?.TsCheck);

                if (failed is not null)
                {
                    await _notifier.NotifyTaskUpdateAsync(ToDto(failed) with { CurrentBitrateKbps = currentBitrate }, CancellationToken.None);
                }
            }
            finally
            {
                _activeSessions.TryRemove(started.Id, out _);
                sessionCompletion.TrySetResult(null);
            }
        }, CancellationToken.None);

        _logger.LogInformation("录制任务启动成功，任务ID: {TaskId}", started.Id);
        return true;
    }

    public bool IsActive(long id)
    {
        return _activeSessions.ContainsKey(id);
    }

    public IReadOnlySet<string> GetActiveOutputPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in _activeSessions.Values)
        {
            if (!string.IsNullOrWhiteSpace(session.OutputPath))
            {
                paths.Add(Path.GetFullPath(session.OutputPath));
            }
        }

        return paths;
    }

    public void RequestStopAllActiveSessions()
    {
        lock (_taskTransitionLock)
        {
            foreach (var session in _activeSessions.Values)
            {
                session.ManualStopRequested = true;
                session.Cancellation.Cancel();
            }
        }
    }

    private static string ResolveRecordPath(string recordPath)
    {
        var basePath = AppContext.BaseDirectory;
        var combined = Path.IsPathRooted(recordPath)
            ? recordPath
            : Path.Combine(basePath, recordPath);
        return Path.GetFullPath(combined);
    }

    private static RecordingTaskDto ToDto(RecordingTask task)
    {
        return new RecordingTaskDto
        {
            Id = task.Id,
            ChannelId = task.ChannelId,
            ChannelName = task.ChannelName,
            TaskName = task.TaskName,
            DisplayName = task.DisplayName,
            IsLive = task.IsLive,
            StartTime = task.StartTime,
            EndTime = task.EndTime,
            Status = task.Status,
            BytesWritten = task.BytesWritten,
            FilePath = task.FilePath,
            ErrorMessage = task.ErrorMessage,
            StartedAt = task.StartedAt,
            FinishedAt = task.FinishedAt,
            PcrElapsedSeconds = task.PcrElapsedSeconds,
            TsCheck = task.TsCheck
        };
    }

    private sealed class RecordingSession
    {
        public RecordingSession(long taskId, string outputPath, bool isLive, DateTimeOffset endTime)
        {
            TaskId = taskId;
            OutputPath = outputPath;
            IsLive = isLive;
            _endTimeUtcTicks = endTime.ToUniversalTime().Ticks;
        }

        public long TaskId { get; }
        public string OutputPath { get; }
        public bool IsLive { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;
        public long BytesWritten { get; set; }
        public bool ManualStopRequested { get; set; }

        private long _endTimeUtcTicks;
        private int _scheduledStopRequested;

        public DateTimeOffset EndTime => new(Interlocked.Read(ref _endTimeUtcTicks), TimeSpan.Zero);
        public bool ScheduledStopRequested => Volatile.Read(ref _scheduledStopRequested) != 0;

        public void UpdateEndTime(DateTimeOffset endTime)
        {
            Interlocked.Exchange(ref _endTimeUtcTicks, endTime.ToUniversalTime().Ticks);
        }

        public bool IsDue(DateTimeOffset now)
        {
            return IsLive && EndTime <= now;
        }

        public bool RequestScheduledStop()
        {
            if (Interlocked.Exchange(ref _scheduledStopRequested, 1) != 0)
            {
                return false;
            }

            Cancellation.Cancel();
            return true;
        }
        
        // 实时码率相关字段
        private long _lastBytesWritten = 0;
        private DateTimeOffset _lastBitrateUpdate = DateTimeOffset.MinValue;
        private double _currentBitrateKbps = 0;
        private readonly object _bitrateLock = new();
        
        // PCR时长相关字段
        private double? _pcrElapsedSeconds = null;
        
        public double? GetPcrElapsedSeconds() => _pcrElapsedSeconds;
        
        public void UpdatePcrElapsedSeconds(double? seconds)
        {
            if (seconds.HasValue)
            {
                _pcrElapsedSeconds = seconds;
            }
        }
        
        public double GetCurrentBitrateKbps()
        {
            lock (_bitrateLock)
            {
                return _currentBitrateKbps;
            }
        }
        
        public void UpdateBitrate(long currentBytes)
        {
            lock (_bitrateLock)
            {
                var now = DateTimeOffset.UtcNow;
                var timeDiff = (now - _lastBitrateUpdate).TotalSeconds;
                
                if (timeDiff >= 1.0 && _lastBytesWritten > 0) // 每秒更新一次
                {
                    var bytesDiff = currentBytes - _lastBytesWritten;
                    _currentBitrateKbps = (bytesDiff * 8.0) / (timeDiff * 1024.0); // Kb/s
                    _lastBytesWritten = currentBytes;
                    _lastBitrateUpdate = now;
                }
                else if (_lastBytesWritten == 0)
                {
                    _lastBytesWritten = currentBytes;
                    _lastBitrateUpdate = now;
                }
            }
        }
    }
}
