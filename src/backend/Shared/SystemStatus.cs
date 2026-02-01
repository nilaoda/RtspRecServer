namespace RtspRecServer.Shared;

public sealed record SystemStatus
{
    public string CurrentUser { get; init; } = string.Empty;
    public DateTimeOffset SystemTime { get; init; }
    public DiskStatus Disk { get; init; } = new();
}
