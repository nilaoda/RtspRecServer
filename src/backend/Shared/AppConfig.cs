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
    public string RecordingTransport { get; init; } = "MP2T/TCP";
    public EpgConfig Epg { get; init; } = new();
}

public sealed record EpgConfig
{
    public string Url { get; init; } = "http://epg.51zmt.top:8000/e.xml.gz";
    public string UpdateTime { get; init; } = "08:00";
    public int CacheDurationHours { get; init; } = 24;
}

public sealed record AppConfigUpdateRequest
{
    public int MaxRecordingTasks { get; init; }
    public string? RecordingTransport { get; init; }
}
