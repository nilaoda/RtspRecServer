namespace RtspRecServer.Shared;

public sealed record RecordingTask
{
    public long Id { get; init; }
    public int ChannelId { get; init; }
    public string ChannelName { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string TaskName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public RecordingStatus Status { get; init; } = RecordingStatus.Pending;
    public string? FilePath { get; init; }
    public long BytesWritten { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}

public sealed record RecordingTaskCreateRequest
{
    public int ChannelId { get; init; }
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public string? TaskName { get; init; }
}

public sealed record RecordingTaskDto
{
    public long Id { get; init; }
    public int ChannelId { get; init; }
    public string ChannelName { get; init; } = string.Empty;
    public string TaskName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public RecordingStatus Status { get; init; }
    public long BytesWritten { get; init; }
    public string? FilePath { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public double? CurrentBitrateKbps { get; init; }
}

public sealed record RecordingTaskStatusUpdate
{
    public long TaskId { get; init; }
    public RecordingStatus Status { get; init; }
    public long BytesWritten { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public double? CurrentBitrateKbps { get; init; }
}

public sealed record DiskStatus
{
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
}

public sealed record RecordingFileInfo
{
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
}

public sealed record MediaInfoResponse
{
    public string Content { get; init; } = string.Empty;
}

public sealed record ApiResponse
{
    public string Message { get; init; } = string.Empty;
    
    public static ApiResponse Create(string message) => new() { Message = message };
}
