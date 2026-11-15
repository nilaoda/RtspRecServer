namespace RtspRecServer.Shared;

public sealed record TsStreamTrack
{
    public int Pid { get; init; }
    public string Label { get; init; } = string.Empty;
}

public sealed record TsCheckSummary
{
    public string Verdict { get; init; } = "unknown";
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
    public long PacketCount { get; init; }
    public long BytesScanned { get; init; }
    public double BitrateKbps { get; init; }
    public string? Video { get; init; }
    public string? Audio { get; init; }
    public TsStreamTrack[] VideoTracks { get; init; } = [];
    public TsStreamTrack[] AudioTracks { get; init; } = [];
    public int PidCount { get; init; }
    public string? LastEvent { get; init; }
}
