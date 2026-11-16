namespace RtspRecServer.Shared;

public sealed record TaskErrorLogEntry
{
    public long Id { get; init; }
    public long Revision { get; init; }
    public long TaskId { get; init; }
    public string TaskName { get; init; } = string.Empty;
    public string ChannelName { get; init; } = string.Empty;
    public string? FileName { get; init; }
    /// <summary>error | warning</summary>
    public string Severity { get; init; } = "warning";
    public string Message { get; init; } = string.Empty;
    /// <summary>去掉时间/次数前缀后的异常身份，用于短时间合并</summary>
    public string Identity { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
}

public sealed record TaskErrorLogSnapshot
{
    public long Generation { get; init; }
    public TaskErrorLogEntry[] Entries { get; init; } = [];
}