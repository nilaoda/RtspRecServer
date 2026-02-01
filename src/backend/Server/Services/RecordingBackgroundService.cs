using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

public sealed class RecordingBackgroundService : BackgroundService
{
    private readonly RecordingManager _manager;
    private readonly ConfigurationService _configurationService;
    private readonly WebSocketConnectionManager _connections;
    private readonly ILogger<RecordingBackgroundService> _logger;

    // 磁盘检查间隔：每 5 秒检查一次
    private int _diskCheckCounter = 0;
    private const int DiskCheckIntervalSeconds = 5;

    public RecordingBackgroundService(
        RecordingManager manager,
        ConfigurationService configurationService,
        WebSocketConnectionManager connections,
        ILogger<RecordingBackgroundService> logger)
    {
        _manager = manager;
        _configurationService = configurationService;
        _connections = connections;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("录制后台服务启动成功");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var tasks = _manager.GetAllTasks();
                var config = _configurationService.GetAppConfig();
                
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
                    if (_manager.ActiveCount >= config.MaxRecordingTasks)
                    {
                        _logger.LogDebug("达到最大录制任务数限制，当前活跃任务数: {ActiveCount}, 最大限制: {MaxTasks}", 
                            _manager.ActiveCount, config.MaxRecordingTasks);
                        break;
                    }

                    if (now < task.StartTime)
                    {
                        continue;
                    }

                    _logger.LogInformation("准备启动录制任务，任务ID: {TaskId}, 任务名称: {TaskName}, 频道: {ChannelName}", 
                        task.Id, task.TaskName, task.ChannelName);

                    // 允许30秒内的延迟启动（仅针对未来任务变成了过去任务的情况，作为一种过载保护）
                    // 但对于回放录制（StartTime本身就是过去的），不应有此限制。
                    // 现在的逻辑简化为：只要是 Pending 且 Now >= StartTime，就启动。
                    // 无论是直播（延迟了30秒也应该尽力录）还是回放（随时可以开始）。
                    // 用户明确指出“30延迟只针对未来时间”，如果需要严格限制直播超时，可以在后续通过任务类型区分。
                    // 目前按照“主要用于回放录制”的需求，移除超时检查。

                    if (task.EndTime <= now)
                    {
                        // 即使结束时间已过，如果是回放录制，也应该允许启动（下载历史视频）。
                        // 除非我们确定这是“直播录制”。
                        // 根据用户反馈“用户输入的时间一般都是过去的”，那么 EndTime <= Now 也是常态（比如下载昨天10点到11点的录像，现在是今天）。
                        // 所以这里也不应该直接失败，应该尝试启动。
                        // 但是，如果 StartTime 和 EndTime 都在过去，TryStartTaskAsync 启动后，
                        // 具体的录制逻辑（ffmpeg/rtsp client）需要能处理“下载并退出”的情况。
                        // 假设 RecordingService 能够处理回放流下载。
                        
                        // 唯一的例外是：如果任务确实无法执行（例如流不支持回放），那会由 TryStartTaskAsync 内部或录制过程报错。
                        // 调度器层应该尽可能宽容。
                    }

                    await _manager.TryStartTaskAsync(task, stoppingToken);
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
