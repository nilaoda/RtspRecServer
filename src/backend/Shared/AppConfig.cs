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
    public bool WriteRecordingLog { get; init; } = true;
    /// <summary>实时错误日志的内存环形缓冲上限（条）</summary>
    public int TaskErrorLogMaxEntries { get; init; } = 10_000;
    public EpgConfig Epg { get; init; } = new();
}

public sealed record EpgConfig
{
    public string Url { get; init; } = "http://epg.51zmt.top:8000/e.xml.gz";
    public string UpdateTime { get; init; } = "08:00";
    public int CacheDurationHours { get; init; } = 24;
}

public sealed record EpgConfigUpdate
{
    public string? Url { get; init; }
    public string? UpdateTime { get; init; }
    public int? CacheDurationHours { get; init; }
}

/// <summary>全量配置更新请求；字段为 null 表示保持原值（密码为空表示不修改）。</summary>
public sealed record AppConfigUpdateRequest
{
    public int? Port { get; init; }
    public string? RecordPath { get; init; }
    public string[]? Host { get; init; }
    public bool? UseAuth { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public int? MaxRecordingTasks { get; init; }
    public string? RecordingTransport { get; init; }
    public bool? WriteRecordingLog { get; init; }
    public int? TaskErrorLogMaxEntries { get; init; }
    public EpgConfigUpdate? Epg { get; init; }
}

/// <summary>暴露给前端的配置视图；不返回明文密码，并附带需要重启才能生效的字段。</summary>
public sealed record AppConfigView
{
    public int Port { get; init; }
    public string RecordPath { get; init; } = string.Empty;
    public string[] Host { get; init; } = [];
    public bool UseAuth { get; init; }
    public string Username { get; init; } = string.Empty;
    public bool HasPassword { get; init; }
    public int MaxRecordingTasks { get; init; }
    public string RecordingTransport { get; init; } = string.Empty;
    public bool WriteRecordingLog { get; init; }
    public int TaskErrorLogMaxEntries { get; init; }
    public EpgConfig Epg { get; init; } = new();
    public string[] RestartRequiredFields { get; init; } = [];
}