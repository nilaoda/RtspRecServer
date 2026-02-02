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
        return _taskStore.GetAll().OrderBy(t => t.Id).ToList();
    }

    public RecordingTask? GetTask(long id)
    {
        return _taskStore.GetById(id);
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
                       t.EndTime == request.EndTime && 
                       t.Status != RecordingStatus.Failed && 
                       t.Status != RecordingStatus.Completed)
            .ToList();

        if (existingTasks.Any())
        {
            _logger.LogWarning("创建录制任务被拒绝：存在相同参数的任务，频道ID: {ChannelId}, 开始时间: {StartTime}, 结束时间: {EndTime}", 
                request.ChannelId, request.StartTime, request.EndTime);
            throw new InvalidOperationException("已存在相同参数的录制任务");
        }

        var suffix = $"{channel.Name}_{request.StartTime:yyyyMMddHHmmss}_{request.EndTime:yyyyMMddHHmmss}";
        var baseName = string.IsNullOrWhiteSpace(request.TaskName)
            ? "Playback"
            : request.TaskName.Trim();
        var taskName = $"{baseName}_{suffix}";

        var task = new RecordingTask
        {
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            Url = channel.Url,
            TaskName = taskName,
            DisplayName = baseName,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            Status = RecordingStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var created = _taskStore.Add(task);
        _logger.LogInformation("录制任务创建成功，任务ID: {TaskId}, 任务名称: {TaskName}, 频道: {ChannelName}", 
            created.Id, created.TaskName, created.ChannelName);
        
        await _notifier.NotifyTaskUpdateAsync(ToDto(created), cancellationToken);
        return created;
    }

    public bool DeleteTask(long id)
    {
        if (_activeSessions.ContainsKey(id))
        {
            return false;
        }

        return _taskStore.Remove(id);
    }

    public async Task<RecordingTask> UpdateTaskAsync(RecordingTask task, CancellationToken cancellationToken)
    {
        var updated = _taskStore.Update(task);
        await _notifier.NotifyTaskUpdateAsync(ToDto(updated), cancellationToken);
        return updated;
    }

    public async Task<bool> StopTaskAsync(long id, CancellationToken cancellationToken)
    {
        _logger.LogInformation("尝试停止录制任务，任务ID: {TaskId}", id);
        
        if (_activeSessions.TryRemove(id, out var session))
        {
            session.ManualStopRequested = true;
            session.Cancellation.Cancel();
            await session.Completion;
            _logger.LogInformation("录制任务停止成功，任务ID: {TaskId}", id);
            return true;
        }

        _logger.LogWarning("停止录制任务失败：任务未在运行中，任务ID: {TaskId}", id);
        return false;
    }

    public async Task<bool> TryStartTaskAsync(RecordingTask task, CancellationToken cancellationToken)
    {
        _logger.LogInformation("尝试启动录制任务，任务ID: {TaskId}, 任务名称: {TaskName}, 频道: {ChannelName}", 
            task.Id, task.TaskName, task.ChannelName);

        if (_activeSessions.ContainsKey(task.Id))
        {
            _logger.LogWarning("录制任务启动失败：任务已在运行中，任务ID: {TaskId}", task.Id);
            return false;
        }

        var config = _configurationService.GetAppConfig();
        var recordPath = ResolveRecordPath(config.RecordPath);
        var fileName = task.TaskName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            ? task.TaskName
            : $"{task.TaskName}.ts";
        var outputPath = Path.Combine(recordPath, fileName);

        _logger.LogInformation("录制任务输出路径: {OutputPath}", outputPath);

        // 如果是回放录制，通常需要在 URL 中附加时间参数。
        // 这里假设基础 URL 不包含时间参数，我们需要根据 StartTime 和 EndTime 构造。
        // 根据常见的 RTSP 回放标准（如海康/大华），格式可能为 rtsp://.../...?starttime=yyyyMMddTHHmmssZ&endtime=...
        // 但不同厂商格式不同。如果 Channel.Url 已经是完整的，则不修改。
        // 用户提到“根据用户输入来动态调整playback参数”，这里做一个简单的追加逻辑示例。
        // 实际生产中可能需要根据 Channel 类型（Hikvision/Dahua/Onvif）来适配。
        // 这里暂且假设如果 URL 中没有 '?'，则追加参数；如果有，则追加 '&starttime=...'
        
        // 简单处理：仅当任务时间是过去时间（暗示回放）时，才尝试附加参数？
        // 或者始终附加？为了安全起见，我们假设 Channel.Url 是纯流地址。
        
        var finalUrl = BuildRecordingUrl(task);

        var started = task with
        {
            Status = RecordingStatus.Recording,
            StartedAt = DateTimeOffset.UtcNow,
            FilePath = outputPath
        };

        _taskStore.Update(started);
        await _notifier.NotifyTaskUpdateAsync(ToDto(started), cancellationToken);

        var session = new RecordingSession(task.Id, outputPath);
        if (!_activeSessions.TryAdd(task.Id, session))
        {
            return false;
        }

        var duration = task.EndTime - task.StartTime;

        session.Completion = Task.Run(async () =>
        {
            var lastUpdate = DateTimeOffset.UtcNow;
            var lastBytes = 0L;
            try
            {
                // 使用 taskWithUrl 而不是 started (started 中的 Url 还是原始的)
                // 注意：RecordingService.RecordAsync 的第一个参数是 RecordingTask，
                // 我们需要确保传进去的 Task 里的 Url 是 finalUrl。
                // 但 started 是用来更新数据库的，数据库里是否要保存带参数的 URL？
                // 通常数据库保留原始配置 URL 更好，运行时 URL 仅用于执行。
                // 所以我们构造一个 runtimeTask 传给 RecordAsync。
                
                var runtimeTask = started with { Url = finalUrl };

                var result = await _recordingService.RecordAsync(runtimeTask, outputPath, duration > TimeSpan.Zero ? duration : null, bytes =>
                {
                    session.BytesWritten = bytes;
                    session.UpdateBitrate(bytes); // 更新实时码率
                    
                    if (DateTimeOffset.UtcNow - lastUpdate > TimeSpan.FromSeconds(1) && bytes != lastBytes)
                    {
                        lastUpdate = DateTimeOffset.UtcNow;
                        lastBytes = bytes;
                        var currentBitrate = session.GetCurrentBitrateKbps();
                        var update = _taskStore.Update(started with
                        {
                            BytesWritten = bytes
                        });
                        _ = _notifier.NotifyTaskUpdateAsync(ToDto(update) with { CurrentBitrateKbps = currentBitrate }, CancellationToken.None);
                    }
                }, session.Cancellation.Token);

                var finalStatus = session.Cancellation.IsCancellationRequested
                    ? session.ManualStopRequested
                        ? RecordingStatus.Stopped
                        : RecordingStatus.Completed
                    : result.Success
                        ? RecordingStatus.Completed
                        : RecordingStatus.Failed;

                var fileBytes = 0L;
                if (File.Exists(outputPath))
                {
                    fileBytes = new FileInfo(outputPath).Length;
                }
                var finalBytesWritten = Math.Max(result.BytesWritten, fileBytes);

                _logger.LogInformation("录制任务完成，任务ID: {TaskId}, 状态: {Status}, 写入字节数: {BytesWritten}", 
                    task.Id, finalStatus, finalBytesWritten);

                var currentBitrate = session.GetCurrentBitrateKbps();
                var completed = _taskStore.Update(started with
                {
                    Status = finalStatus,
                    BytesWritten = finalBytesWritten,
                    ErrorMessage = result.ErrorMessage,
                    FinishedAt = DateTimeOffset.UtcNow
                });

                await _notifier.NotifyTaskUpdateAsync(ToDto(completed) with { CurrentBitrateKbps = currentBitrate }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "录制任务失败 {TaskId}, 错误信息: {ErrorMessage}", task.Id, ex.Message);
                var currentBitrate = session.GetCurrentBitrateKbps();
                var failed = _taskStore.Update(started with
                {
                    Status = RecordingStatus.Failed,
                    ErrorMessage = ex.Message,
                    FinishedAt = DateTimeOffset.UtcNow
                });
                await _notifier.NotifyTaskUpdateAsync(ToDto(failed) with { CurrentBitrateKbps = currentBitrate }, CancellationToken.None);
            }
            finally
            {
                _activeSessions.TryRemove(task.Id, out _);
            }
        }, cancellationToken);

        _logger.LogInformation("录制任务启动成功，任务ID: {TaskId}", task.Id);
        return true;
    }

    public bool IsActive(long id)
    {
        return _activeSessions.ContainsKey(id);
    }

    public void RequestStopAllActiveSessions()
    {
        foreach (var session in _activeSessions.Values)
        {
            session.ManualStopRequested = true;
            session.Cancellation.Cancel();
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
            StartTime = task.StartTime,
            EndTime = task.EndTime,
            Status = task.Status,
            BytesWritten = task.BytesWritten,
            FilePath = task.FilePath,
            ErrorMessage = task.ErrorMessage,
            StartedAt = task.StartedAt,
            FinishedAt = task.FinishedAt
        };
    }

    private static string BuildRecordingUrl(RecordingTask task)
    {
        var url = task.Url;
        if (url.Contains("playseek=", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        if (task.StartTime > DateTimeOffset.UtcNow)
        {
            return url;
        }

        if (url.Contains("/PLTV/", StringComparison.OrdinalIgnoreCase))
        {
            var startLocal = task.StartTime.ToLocalTime().ToString("yyyyMMddHHmmss");
            var endLocal = task.EndTime.ToLocalTime().AddMinutes(1).ToString("yyyyMMddHHmmss");
            var separator = url.Contains('?') ? '&' : '?';
            return $"{url}{separator}playseek={startLocal}-{endLocal}";
        }

        if (url.Contains("starttime=", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var startUtc = task.StartTime.ToUniversalTime().ToString("yyyyMMddTHHmmssZ");
        var endUtc = task.EndTime.ToUniversalTime().AddMinutes(1).ToString("yyyyMMddTHHmmssZ");
        var querySeparator = url.Contains('?') ? '&' : '?';
        return $"{url}{querySeparator}starttime={startUtc}&endtime={endUtc}";
    }

    private sealed class RecordingSession
    {
        public RecordingSession(long taskId, string outputPath)
        {
            TaskId = taskId;
            OutputPath = outputPath;
        }

        public long TaskId { get; }
        public string OutputPath { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;
        public long BytesWritten { get; set; }
        public bool ManualStopRequested { get; set; }
        
        // 实时码率相关字段
        private long _lastBytesWritten = 0;
        private DateTimeOffset _lastBitrateUpdate = DateTimeOffset.MinValue;
        private double _currentBitrateKbps = 0;
        private readonly object _bitrateLock = new();
        
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
                    _currentBitrateKbps = (bytesDiff * 8.0) / (timeDiff * 1024.0); // KB/s
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
