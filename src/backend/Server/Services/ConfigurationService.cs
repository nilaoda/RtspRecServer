using System.Globalization;
using System.Text.Json;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

public sealed class ConfigurationService
{
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly string _basePath;
    private readonly string _appConfigPath;
    private readonly string _channelsPath;
    private readonly AppConfig _bootConfig;

    public ConfigurationService()
        : this(AppContext.BaseDirectory)
    {
    }

    internal ConfigurationService(string basePath)
    {
        _basePath = basePath;
        _appConfigPath = Path.Combine(_basePath, "recsettings.json");
        _channelsPath = Path.Combine(_basePath, "channels.json");
        // 启动时生效的配置快照，用于提示哪些修改需要重启服务
        _bootConfig = GetAppConfig();
    }

    internal string BasePath => _basePath;

    public AppConfig GetAppConfig()
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            if (!File.Exists(_appConfigPath))
            {
                _lock.EnterWriteLock();
                try
                {
                    WriteJson(_appConfigPath, new AppConfig());
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }

            var json = File.ReadAllText(_appConfigPath);
            return DeserializeAppConfig(json);
        }
        finally
        {
            _lock.ExitUpgradeableReadLock();
        }
    }

    public AppConfig UpdateAppConfig(AppConfigUpdateRequest request)
    {
        _lock.EnterWriteLock();
        try
        {
            var current = File.Exists(_appConfigPath)
                ? DeserializeAppConfig(File.ReadAllText(_appConfigPath))
                : new AppConfig();

            var port = request.Port ?? current.Port;
            if (port is < 1 or > 65535)
            {
                throw new InvalidOperationException("端口必须在 1-65535 之间");
            }

            var recordPath = request.RecordPath is null ? current.RecordPath : request.RecordPath.Trim();
            if (string.IsNullOrWhiteSpace(recordPath))
            {
                throw new InvalidOperationException("录制目录不能为空");
            }

            var host = request.Host is null ? current.Host : NormalizeHosts(request.Host);
            if (host.Length == 0)
            {
                throw new InvalidOperationException("监听地址不能为空");
            }

            var username = request.Username is null ? current.Username : request.Username.Trim();
            if (string.IsNullOrWhiteSpace(username))
            {
                throw new InvalidOperationException("用户名不能为空");
            }

            // 密码为空表示保持原密码（前端不回显明文密码）
            var password = string.IsNullOrEmpty(request.Password) ? current.Password : request.Password;

            var maxRecordingTasks = request.MaxRecordingTasks ?? current.MaxRecordingTasks;
            if (maxRecordingTasks < 1)
            {
                throw new InvalidOperationException("最大并发必须大于 0");
            }

            var taskErrorLogMaxEntries = request.TaskErrorLogMaxEntries ?? current.TaskErrorLogMaxEntries;
            if (taskErrorLogMaxEntries is < 100 or > 50_000)
            {
                throw new InvalidOperationException("实时错误日志上限必须在 100-50000 条之间");
            }

            var transport = string.IsNullOrWhiteSpace(request.RecordingTransport)
                ? current.RecordingTransport
                : request.RecordingTransport.Trim();
            if (transport is not ("MP2T/TCP" or "MP2T/RTP/TCP"))
            {
                throw new InvalidOperationException("不支持的封装方式");
            }

            var epg = current.Epg;
            if (request.Epg is { } epgUpdate)
            {
                var url = epgUpdate.Url is null ? epg.Url : epgUpdate.Url.Trim();
                if (string.IsNullOrWhiteSpace(url))
                {
                    throw new InvalidOperationException("节目表地址不能为空");
                }

                var updateTime = epgUpdate.UpdateTime is null ? epg.UpdateTime : epgUpdate.UpdateTime.Trim();
                if (!IsValidClock(updateTime))
                {
                    throw new InvalidOperationException("节目表更新时间格式必须为 HH:mm");
                }

                var cacheHours = epgUpdate.CacheDurationHours ?? epg.CacheDurationHours;
                if (cacheHours is < 1 or > 24 * 30)
                {
                    throw new InvalidOperationException("节目表缓存时长必须在 1-720 小时之间");
                }

                epg = new EpgConfig
                {
                    Url = url,
                    UpdateTime = updateTime,
                    CacheDurationHours = cacheHours
                };
            }

            var updated = current with
            {
                Port = port,
                RecordPath = recordPath,
                Host = host,
                UseAuth = request.UseAuth ?? current.UseAuth,
                Username = username,
                Password = password,
                MaxRecordingTasks = maxRecordingTasks,
                RecordingTransport = transport,
                WriteRecordingLog = request.WriteRecordingLog ?? current.WriteRecordingLog,
                TaskErrorLogMaxEntries = taskErrorLogMaxEntries,
                Epg = epg
            };
            WriteJson(_appConfigPath, updated);
            return updated;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public AppConfigView GetAppConfigView()
    {
        return ToView(GetAppConfig());
    }

    private AppConfigView ToView(AppConfig config)
    {
        return new AppConfigView
        {
            Port = config.Port,
            RecordPath = config.RecordPath,
            Host = config.Host,
            UseAuth = config.UseAuth,
            Username = config.Username,
            HasPassword = !string.IsNullOrEmpty(config.Password),
            MaxRecordingTasks = config.MaxRecordingTasks,
            RecordingTransport = config.RecordingTransport,
            WriteRecordingLog = config.WriteRecordingLog,
            TaskErrorLogMaxEntries = config.TaskErrorLogMaxEntries,
            Epg = config.Epg,
            RestartRequiredFields = GetRestartRequiredFields(config)
        };
    }

    private string[] GetRestartRequiredFields(AppConfig config)
    {
        var fields = new List<string>(2);
        if (config.Port != _bootConfig.Port)
        {
            fields.Add("port");
        }
        if (!HostsEqual(config.Host, _bootConfig.Host))
        {
            fields.Add("host");
        }
        return fields.ToArray();
    }

    private static string[] NormalizeHosts(IEnumerable<string> hosts)
    {
        return hosts
            .Select(host => host?.Trim() ?? string.Empty)
            .Where(host => host.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool HostsEqual(string[] left, string[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }
        return left
            .Select(host => host.Trim())
            .OrderBy(host => host, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(
                right.Select(host => host.Trim()).OrderBy(host => host, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsValidClock(string value)
    {
        return TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    public List<ChannelConfig> GetChannels()
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            if (!File.Exists(_channelsPath))
            {
                _lock.EnterWriteLock();
                try
                {
                    WriteJson(_channelsPath, new List<ChannelConfig>());
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }

            var json = File.ReadAllText(_channelsPath);
            return JsonSerializer.Deserialize(json, RtspJsonContext.Default.ListChannelConfig) ?? new List<ChannelConfig>();
        }
        finally
        {
            _lock.ExitUpgradeableReadLock();
        }
    }

    public ChannelConfig AddChannel(ChannelConfig channel)
    {
        _lock.EnterWriteLock();
        try
        {
            var channels = File.Exists(_channelsPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(_channelsPath), RtspJsonContext.Default.ListChannelConfig) ?? new List<ChannelConfig>()
                : new List<ChannelConfig>();

            var nextId = channels.Count == 0 ? 0 : channels.Max(c => c.Id) + 1;
            var created = channel with { Id = nextId };
            channels.Add(created);
            WriteJson(_channelsPath, channels);
            return created;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public ChannelImportResult AddChannels(IReadOnlyList<ChannelImportItem>? items)
    {
        if (items is null || items.Count == 0)
        {
            throw new InvalidOperationException("没有可导入的频道");
        }

        _lock.EnterWriteLock();
        try
        {
            var channels = File.Exists(_channelsPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(_channelsPath), RtspJsonContext.Default.ListChannelConfig) ?? new List<ChannelConfig>()
                : new List<ChannelConfig>();

            var nextId = channels.Count == 0 ? 0 : channels.Max(c => c.Id) + 1;
            var created = new List<ChannelConfig>(items.Count);
            foreach (var item in items)
            {
                if (item is null)
                {
                    throw new InvalidOperationException("频道名称和地址不能为空");
                }

                var name = item.Name?.Trim() ?? string.Empty;
                var url = item.Url?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                {
                    throw new InvalidOperationException("频道名称和地址不能为空");
                }

                var channel = new ChannelConfig
                {
                    Id = nextId++,
                    Name = name,
                    Url = url
                };
                channels.Add(channel);
                created.Add(channel);
            }

            WriteJson(_channelsPath, channels);
            return new ChannelImportResult
            {
                AddedCount = created.Count,
                Channels = created
            };
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public ChannelConfig? UpdateChannel(int id, ChannelConfig channel)
    {
        _lock.EnterWriteLock();
        try
        {
            var channels = File.Exists(_channelsPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(_channelsPath), RtspJsonContext.Default.ListChannelConfig) ?? new List<ChannelConfig>()
                : new List<ChannelConfig>();

            var index = channels.FindIndex(c => c.Id == id);
            if (index < 0)
            {
                return null;
            }

            var updated = channel with { Id = id };
            channels[index] = updated;
            WriteJson(_channelsPath, channels);
            return updated;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool DeleteChannel(int id)
    {
        _lock.EnterWriteLock();
        try
        {
            var channels = File.Exists(_channelsPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(_channelsPath), RtspJsonContext.Default.ListChannelConfig) ?? new List<ChannelConfig>()
                : new List<ChannelConfig>();

            var removed = channels.RemoveAll(c => c.Id == id) > 0;
            if (removed)
            {
                WriteJson(_channelsPath, channels);
            }

            return removed;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public ChannelBulkResult DeleteChannels(IReadOnlyCollection<int>? ids)
    {
        var requestedIds = (ids ?? [])
            .Where(id => id >= 0)
            .Distinct()
            .ToArray();
        if (requestedIds.Length == 0)
        {
            return new ChannelBulkResult();
        }

        _lock.EnterWriteLock();
        try
        {
            var channels = File.Exists(_channelsPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(_channelsPath), RtspJsonContext.Default.ListChannelConfig) ?? new List<ChannelConfig>()
                : new List<ChannelConfig>();
            var existingIds = channels.Select(channel => channel.Id).ToHashSet();
            var succeededIds = requestedIds.Where(existingIds.Contains).ToArray();
            var failedIds = requestedIds.Where(id => !existingIds.Contains(id)).ToArray();

            if (succeededIds.Length > 0)
            {
                var succeededSet = succeededIds.ToHashSet();
                channels.RemoveAll(channel => succeededSet.Contains(channel.Id));
                WriteJson(_channelsPath, channels);
            }

            return new ChannelBulkResult
            {
                Succeeded = succeededIds.Length,
                Failed = failedIds.Length,
                SucceededIds = succeededIds,
                FailedIds = failedIds
            };
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private static AppConfig DeserializeAppConfig(string json)
    {
        var config = JsonSerializer.Deserialize(json, RtspJsonContext.Default.AppConfig) ?? new AppConfig();
        // 源生成器会把缺失的布尔字段写成 false；旧配置应默认开启录制日志。
        if (!json.Contains("\"writeRecordingLog\"", StringComparison.OrdinalIgnoreCase))
        {
            config = config with { WriteRecordingLog = true };
        }

        // 兼容旧配置缺失或为空的字段，避免运行期拿到 null/0
        if (config.Port is < 1 or > 65535)
        {
            config = config with { Port = 8080 };
        }
        if (string.IsNullOrWhiteSpace(config.RecordPath))
        {
            config = config with { RecordPath = "./records" };
        }
        var hosts = NormalizeHosts(config.Host ?? []);
        if (hosts.Length == 0)
        {
            hosts = ["0.0.0.0"];
        }
        if (config.Host is null || !hosts.SequenceEqual(config.Host, StringComparer.OrdinalIgnoreCase))
        {
            config = config with { Host = hosts };
        }
        if (string.IsNullOrWhiteSpace(config.Username))
        {
            config = config with { Username = "admin" };
        }
        if (string.IsNullOrEmpty(config.Password))
        {
            config = config with { Password = "admin" };
        }
        if (config.MaxRecordingTasks < 1)
        {
            config = config with { MaxRecordingTasks = 1 };
        }
        if (config.TaskErrorLogMaxEntries is < 100 or > 50_000)
        {
            config = config with { TaskErrorLogMaxEntries = 10_000 };
        }
        if (string.IsNullOrWhiteSpace(config.RecordingTransport))
        {
            config = config with { RecordingTransport = "MP2T/TCP" };
        }

        var defaults = new EpgConfig();
        var epg = config.Epg ?? defaults;
        var epgUrl = string.IsNullOrWhiteSpace(epg.Url) ? defaults.Url : epg.Url;
        var updateTime = IsValidClock(epg.UpdateTime) ? epg.UpdateTime : defaults.UpdateTime;
        var cacheHours = epg.CacheDurationHours is < 1 or > 720 ? defaults.CacheDurationHours : epg.CacheDurationHours;
        if (epg.Url != epgUrl || epg.UpdateTime != updateTime || epg.CacheDurationHours != cacheHours)
        {
            config = config with
            {
                Epg = new EpgConfig
                {
                    Url = epgUrl,
                    UpdateTime = updateTime,
                    CacheDurationHours = cacheHours
                }
            };
        }

        return config;
    }

    private static void WriteJson<T>(string path, T value)
    {
        var typeInfo = RtspJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new InvalidOperationException($"JsonTypeInfo not found for {typeof(T).Name}");
        var json = JsonSerializer.Serialize(value, typeInfo);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, true);
    }
}
