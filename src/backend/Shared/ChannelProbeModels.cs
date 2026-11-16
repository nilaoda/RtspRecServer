namespace RtspRecServer.Shared;

public sealed record ChannelProbeRequest
{
    public int[] ChannelIds { get; init; } = [];
    /// <summary>并行检测数量，默认 3，范围 1-10</summary>
    public int Concurrency { get; init; } = 3;
    /// <summary>失败后的重试次数，默认 0，范围 0-5</summary>
    public int Retries { get; init; }
}

public sealed record ChannelBulkRequest
{
    public int[] ChannelIds { get; init; } = [];
}

public sealed record ChannelBulkResult
{
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int[] SucceededIds { get; init; } = [];
    public int[] FailedIds { get; init; } = [];
}

public sealed record ChannelProbeResult
{
    public int ChannelId { get; init; }
    public bool Accessible { get; init; }
    public TsStreamTrack[] VideoTracks { get; init; } = [];
    public TsStreamTrack[] AudioTracks { get; init; } = [];
    public string? Resolution { get; init; }
    public double? BitrateKbps { get; init; }
    public string? TsVerdict { get; init; }
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset CheckedAt { get; init; }
    public double ElapsedSeconds { get; init; }
    public int Attempts { get; init; } = 1;
}
