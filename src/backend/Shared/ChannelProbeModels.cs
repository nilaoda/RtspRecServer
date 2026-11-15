namespace RtspRecServer.Shared;

public sealed record ChannelProbeRequest
{
    public int[] ChannelIds { get; init; } = [];
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
}
