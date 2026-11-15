namespace RtspRecServer.Shared;

/// <summary>
/// 重复定时录制规则（Tvheadend timer 能力）：按频道 + 每周几 + 每日时间窗口自动生成录制任务。
/// </summary>
public sealed record RecordingTimer
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int ChannelId { get; init; }

    // 频道名称快照：频道被删除后规则仍在，前端据此提示"频道缺失"
    public string ChannelName { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;

    // "HH:mm"；EndTime <= StartTime 表示跨午夜窗口（如 23:30-01:00）
    public string StartTime { get; init; } = string.Empty;
    public string EndTime { get; init; } = string.Empty;

    // 7 位布尔，0=周一 … 6=周日
    public bool[] Weekdays { get; init; } = [false, false, false, false, false, false, false];
    public int PreOffsetMinutes { get; init; }
    public int PostOffsetMinutes { get; init; }

    // 可选生效日期范围，"yyyy-MM-dd"，null 表示不限
    public string? StartDate { get; init; }
    public string? EndDate { get; init; }
    public bool Enabled { get; init; } = true;

    // 最近一次已生成任务的窗口起点（防同窗口重复生成，重启后仍有效）
    public DateTimeOffset? LastTriggeredAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record TimerUpsertRequest
{
    public string Name { get; init; } = string.Empty;
    public int ChannelId { get; init; }
    public string StartTime { get; init; } = string.Empty;
    public string EndTime { get; init; } = string.Empty;
    public bool[] Weekdays { get; init; } = [false, false, false, false, false, false, false];
    public int PreOffsetMinutes { get; init; }
    public int PostOffsetMinutes { get; init; }
    public string? StartDate { get; init; }
    public string? EndDate { get; init; }
    public bool Enabled { get; init; } = true;
}
