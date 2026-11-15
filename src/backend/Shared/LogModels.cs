namespace RtspRecServer.Shared;

public sealed record LogFileInfo
{
    public string FileName { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
}

public sealed record LogFileContent
{
    public string FileName { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public bool Truncated { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
}
