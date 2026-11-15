using System.Globalization;
using RtspRecServer.Server.Recording;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

/// <summary>
/// 定时录制规则的业务服务：CRUD 校验 + 每秒扫描窗口并自动生成录制任务。
/// </summary>
public sealed class RecordingTimerService
{
    /// <summary>预生成未来窗口任务的天数：主任务表可见三天内的自动录制计划</summary>
    public const int LookAheadDays = 3;

    private readonly RecordingTimerStore _timerStore;
    private readonly ConfigurationService _configurationService;
    private readonly RecordingManager _recordingManager;
    private readonly ILogger<RecordingTimerService> _logger;

    public RecordingTimerService(
        RecordingTimerStore timerStore,
        ConfigurationService configurationService,
        RecordingManager recordingManager,
        ILogger<RecordingTimerService> logger)
    {
        _timerStore = timerStore;
        _configurationService = configurationService;
        _recordingManager = recordingManager;
        _logger = logger;
    }

    public List<RecordingTimer> GetAll()
    {
        return _timerStore.GetAll().OrderBy(t => t.Id).ToList();
    }

    public RecordingTimer Create(TimerUpsertRequest request)
    {
        var timer = ValidateAndBuild(request);
        var created = _timerStore.Add(timer);
        _logger.LogInformation("定时规则创建成功，ID: {TimerId}, 名称: {Name}, 频道: {ChannelName}",
            created.Id, created.Name, created.ChannelName);
        return created;
    }

    public RecordingTimer? Update(long id, TimerUpsertRequest request)
    {
        var validated = ValidateAndBuild(request);
        var updated = _timerStore.TryUpdate(id, current => validated with
        {
            Id = current.Id,
            // 规则变更后重置触发记录，让扫描按新配置重新预生成
            LastTriggeredAt = null,
            CreatedAt = current.CreatedAt
        });
        if (updated is not null)
        {
            _logger.LogInformation("定时规则更新成功，ID: {TimerId}, 名称: {Name}", updated.Id, updated.Name);
            // 旧配置预生成的待录任务不再匹配新规则，清除后由扫描按新配置重新生成
            _recordingManager.RemovePendingTasksByTimer(id);
        }
        return updated;
    }

    public bool Delete(long id)
    {
        var removed = _timerStore.Remove(id);
        if (removed)
        {
            _logger.LogInformation("定时规则删除成功，ID: {TimerId}", id);
            _recordingManager.RemovePendingTasksByTimer(id);
        }
        return removed;
    }

    /// <summary>
    /// 由 RecordingBackgroundService 每秒调用：扫描启用规则的窗口并预生成录制任务。
    /// 为未来 {LookAheadDays} 天内的窗口预创建 Pending 任务，主任务表可见即将录制的计划（类似 Tvheadend）。
    /// LastTriggeredAt 记录最近生成的窗口起点，保证每个窗口只生成一次（重启后依然有效，
    /// 窗口内手动停止或用户删除预生成任务也不会重建）。
    /// </summary>
    public async Task ScanAndGenerateAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var timers = _timerStore.GetAll().Where(t => t.Enabled).ToList();
        if (timers.Count == 0)
        {
            return;
        }

        var channels = _configurationService.GetChannels();
        var allTasks = _recordingManager.GetAllTasks();
        var lookAheadEnd = now.AddDays(LookAheadDays);
        var todayLocal = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).Date;

        foreach (var timer in timers)
        {
            // 从昨天开始枚举（覆盖跨午夜的在播窗口），筛选出未触发、仍在进行或三天内即将开始的窗口
            var windows = TimerWindowCalculator
                .EnumerateWindows(timer, todayLocal.AddDays(-1), LookAheadDays + 2, TimeZoneInfo.Local)
                .Where(w =>
                    w.EffectiveEnd > now &&
                    w.EffectiveStart <= lookAheadEnd &&
                    (timer.LastTriggeredAt is null || timer.LastTriggeredAt < w.WindowStart));

            foreach (var window in windows)
            {
                var channel = channels.FirstOrDefault(c => c.Id == timer.ChannelId);
                if (channel is null)
                {
                    _logger.LogWarning("定时规则 {TimerId} 引用的频道不存在（频道ID: {ChannelId}），跳过窗口 {Start}",
                        timer.Id, timer.ChannelId, window.WindowStart);
                    MarkTriggered(timer.Id, window);
                    continue;
                }

                // 已被同频道的活跃任务覆盖（如用户手动预约过），本窗口视为已处理
                if (allTasks.Any(t =>
                        t.ChannelId == timer.ChannelId &&
                        (t.Status == RecordingStatus.Pending || t.Status == RecordingStatus.Recording) &&
                        t.StartTime < window.WindowEnd && t.EndTime > window.WindowStart))
                {
                    MarkTriggered(timer.Id, window);
                    continue;
                }

                var start = window.EffectiveStart;
                var end = window.EffectiveEnd;
                // 直播频道错过窗口开头时从当前时间起录剩余部分；未来窗口保持完整时间，由调度器准点启动
                if (start < now && LiveSourceClassifier.IsLive(channel.Url))
                {
                    start = now;
                }

                try
                {
                    var created = await _recordingManager.CreateTaskAsync(new RecordingTaskCreateRequest
                    {
                        ChannelId = timer.ChannelId,
                        StartTime = start,
                        EndTime = end,
                        TaskName = timer.Name,
                        TimerId = timer.Id
                    }, cancellationToken);

                    _logger.LogInformation("定时规则 {TimerId} 已生成录制任务，任务ID: {TaskId}, 窗口: {Start} ~ {End}",
                        timer.Id, created.Id, start, end);
                }
                catch (InvalidOperationException ex)
                {
                    // 业务性冲突（如已存在相同参数任务）：视为本窗口已处理，避免每秒重试
                    _logger.LogWarning(ex, "定时规则 {TimerId} 生成任务被跳过：{Message}", timer.Id, ex.Message);
                }

                MarkTriggered(timer.Id, window);
            }
        }
    }

    private void MarkTriggered(long timerId, TimerWindow window)
    {
        _timerStore.TryUpdate(timerId, t => t with { LastTriggeredAt = window.WindowStart });
    }

    private RecordingTimer ValidateAndBuild(TimerUpsertRequest request)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            throw new InvalidOperationException("请填写规则名称");
        }

        var channel = _configurationService.GetChannels().FirstOrDefault(c => c.Id == request.ChannelId);
        if (channel is null)
        {
            throw new InvalidOperationException("频道不存在");
        }

        if (!TimeOnly.TryParseExact(request.StartTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startTime))
        {
            throw new InvalidOperationException("开始时间格式不正确");
        }

        if (!TimeOnly.TryParseExact(request.EndTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endTime))
        {
            throw new InvalidOperationException("结束时间格式不正确");
        }

        if (startTime == endTime)
        {
            throw new InvalidOperationException("结束时间必须与开始时间不同");
        }

        if (request.Weekdays is null || request.Weekdays.Length != 7 || !request.Weekdays.Any(w => w))
        {
            throw new InvalidOperationException("请至少选择一个星期");
        }

        if (request.PreOffsetMinutes < 0 || request.PostOffsetMinutes < 0 ||
            request.PreOffsetMinutes > 1440 || request.PostOffsetMinutes > 1440)
        {
            throw new InvalidOperationException("录制余量需在 0 ~ 1440 分钟之间");
        }

        ValidateDateRange(request.StartDate);
        ValidateDateRange(request.EndDate);
        var startDate = ParseDateOrNull(request.StartDate, "生效开始日期");
        var endDate = ParseDateOrNull(request.EndDate, "生效结束日期");
        if (startDate.HasValue && endDate.HasValue && startDate.Value > endDate.Value)
        {
            throw new InvalidOperationException("生效开始日期不能晚于结束日期");
        }

        return new RecordingTimer
        {
            Name = name,
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            Url = channel.Url,
            StartTime = startTime.ToString("HH:mm"),
            EndTime = endTime.ToString("HH:mm"),
            Weekdays = request.Weekdays.ToArray(),
            PreOffsetMinutes = request.PreOffsetMinutes,
            PostOffsetMinutes = request.PostOffsetMinutes,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Enabled = request.Enabled
        };
    }

    private static void ValidateDateRange(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new InvalidOperationException("日期格式不正确，应为 yyyy-MM-dd");
        }
    }

    private static DateOnly? ParseDateOrNull(string? value, string fieldLabel)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
