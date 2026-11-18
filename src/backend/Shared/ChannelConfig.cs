namespace RtspRecServer.Shared;

public sealed record ChannelConfig
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string? Group { get; init; }
}

public sealed record ChannelImportItem
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string? Group { get; init; }
}

public sealed record ChannelImportRequest
{
    public List<ChannelImportItem> Channels { get; init; } = new();
}

public sealed record ChannelBulkEditItem
{
    public int Id { get; init; }
    public string? Name { get; init; }
    public string? Url { get; init; }
    public string? Group { get; init; }
    public bool ClearGroup { get; init; }
}

public sealed record ChannelBulkEditRequest
{
    public List<ChannelBulkEditItem> Channels { get; init; } = new();
}

public sealed record ChannelBulkEditFailure
{
    public int Id { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record ChannelBulkEditResult
{
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int[] SucceededIds { get; init; } = [];
    public ChannelBulkEditFailure[] FailedItems { get; init; } = [];
}

public sealed record ChannelImportResult
{
    public int AddedCount { get; init; }
    public List<ChannelConfig> Channels { get; init; } = new();
}
