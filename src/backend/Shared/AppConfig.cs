namespace RtspRecServer.Shared;

public sealed record AppConfig
{
    public int Port { get; init; } = 8080;
    public string RecordPath { get; init; } = "./records";
    public string[] Host { get; init; } = ["0.0.0.0"];
    public bool UseAuth { get; init; }
    public string Username { get; init; } = "admin";
    public string Password { get; init; } = "admin";
    public int MaxRecordingTasks { get; init; } = 1;
}

public sealed record AppConfigUpdateRequest
{
    public int MaxRecordingTasks { get; init; }
}
