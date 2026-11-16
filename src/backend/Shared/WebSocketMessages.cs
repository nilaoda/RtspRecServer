namespace RtspRecServer.Shared;

public sealed record TaskUpdateMessage
{
    public string Type { get; init; } = "taskUpdated";
    public RecordingTaskDto Task { get; init; } = new();
}

public sealed record SystemStatusMessage
{
    public string Type { get; init; } = "systemStatus";
    public SystemStatus Status { get; init; } = new();
}

public sealed record TaskErrorLogMessage
{
    public string Type { get; init; } = "taskErrorLog";
    public long Generation { get; init; }
    public bool Reset { get; init; }
    public TaskErrorLogEntry[] Entries { get; init; } = [];
}
