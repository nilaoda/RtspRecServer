using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

public sealed class RecordingBackgroundService : BackgroundService
{
    private readonly RecordingManager _manager;
    private readonly ConfigurationService _configurationService;
    private readonly RecordingTimerService _timerService;
    private readonly WebSocketConnectionManager _connections;
    private readonly ILogger<RecordingBackgroundService> _logger;

    // 磁盘检查间隔：每 5 秒检查一次
    private int _diskCheckCounter = 0;
    private const int DiskCheckIntervalSeconds = 5;

    public RecordingBackgroundService(
        RecordingManager manager,
        ConfigurationService configurationService,
        RecordingTimerService timerService,
        WebSocketConnectionManager connections,
        ILogger<RecordingBackgroundService> logger)
    {
        _manager = manager;
        _configurationService = configurationService;
        _timerService = timerService;
        _connections = connections;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("录制后台服务启动成功");

        await _manager.ReconcileInterruptedTasksAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var config = _configurationService.GetAppConfig();

                _manager.StopSessionsDue(now);

                // 扫描定时规则，命中窗口则自动生成录制任务
                await _timerService.ScanAndGenerateAsync(now, stoppingToken);

                var tasks = _manager.GetAllTasks();
                _logger.LogDebug("录制调度循环开始，当前任务数: {TaskCount}", tasks.Count);

                // 推送系统状态（每 5 秒）
                if (++_diskCheckCounter >= DiskCheckIntervalSeconds)
                {
                    _diskCheckCounter = 0;
                    await PushSystemStatusAsync(config, stoppingToken);
                }

                var pending = tasks
                    .Where(t => t.Status == RecordingStatus.Pending)
                    .OrderBy(t => t.StartTime)
                    .ToList();

                foreach (var task in pending)
                {
                    if (task.IsLive && task.EndTime <= now)
                    {
                        await _manager.FailMissedTaskAsync(task, stoppingToken);
                        continue;
                    }

                    if (now < task.StartTime)
                    {
                        continue;
                    }

                    if (task.IsLive)
                    {
                        await _manager.TryStartTaskAsync(task, stoppingToken, config);
                        continue;
                    }

                    // 近实时任务需要等待回放片段生成：开始时间在创建时刻前后 1 分钟内视为近实时
                    var needsTimeshiftDelay = task.StartTime >= task.CreatedAt.AddMinutes(-1);
                    // 对近实时任务，统一等待到开始时间 + 1 分钟再启动
                    if (needsTimeshiftDelay && now < task.StartTime.AddMinutes(1))
                    {
                        continue;
                    }

                    await _manager.TryStartTaskAsync(task, stoppingToken, config);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "录制调度异常: {ErrorMessage}", ex.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task PushSystemStatusAsync(AppConfig config, CancellationToken cancellationToken)
    {
        try
        {
            var disk = GetDiskStatus(config.RecordPath);
            var status = new SystemStatus
            {
                CurrentUser = Environment.UserName,
                SystemTime = DateTimeOffset.UtcNow,
                Disk = disk
            };
            await _connections.BroadcastSystemStatusAsync(status, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "推送系统状态失败");
        }
    }

    private static DiskStatus GetDiskStatus(string recordPath)
    {
        try
        {
            var fullPath = ResolveRecordPath(recordPath);
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady).ToList();
            
            var match = drives
                .Select(d => new { Drive = d, Root = d.RootDirectory.FullName })
                .Where(d => fullPath.StartsWith(d.Root, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.Root.Length)
                .FirstOrDefault();

            if (match != null)
            {
                return new DiskStatus
                {
                    TotalBytes = match.Drive.TotalSize,
                    FreeBytes = match.Drive.AvailableFreeSpace
                };
            }
        }
        catch
        {
            // Ignore
        }

        return new DiskStatus();
    }

    private static string ResolveRecordPath(string recordPath)
    {
        var basePath = AppContext.BaseDirectory;
        var safePath = string.IsNullOrWhiteSpace(recordPath) ? "./records" : recordPath;
        var combined = Path.IsPathRooted(safePath) ? safePath : Path.Combine(basePath, safePath);
        return Path.GetFullPath(combined);
    }
}
