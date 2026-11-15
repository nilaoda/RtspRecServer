namespace RtspRecServer.Shared;

public sealed record ChannelConfig
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
}

public sealed record ChannelImportItem
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
}

public sealed record ChannelImportRequest
{
    public List<ChannelImportItem> Channels { get; init; } = new();
}

public sealed record ChannelImportResult
{
    public int AddedCount { get; init; }
    public List<ChannelConfig> Channels { get; init; } = new();
}
