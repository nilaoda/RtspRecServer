using System.Text.Json;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

public sealed class ConfigurationService
{
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly string _basePath;
    private readonly string _appConfigPath;
    private readonly string _channelsPath;

    public ConfigurationService()
        : this(AppContext.BaseDirectory)
    {
    }

    internal ConfigurationService(string basePath)
    {
        _basePath = basePath;
        _appConfigPath = Path.Combine(_basePath, "recsettings.json");
        _channelsPath = Path.Combine(_basePath, "channels.json");
    }

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

            var transport = string.IsNullOrWhiteSpace(request.RecordingTransport)
                ? current.RecordingTransport
                : request.RecordingTransport;
            var updated = current with
            {
                MaxRecordingTasks = request.MaxRecordingTasks,
                RecordingTransport = transport,
                WriteRecordingLog = request.WriteRecordingLog ?? current.WriteRecordingLog
            };
            WriteJson(_appConfigPath, updated);
            return updated;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
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

    private static AppConfig DeserializeAppConfig(string json)
    {
        var config = JsonSerializer.Deserialize(json, RtspJsonContext.Default.AppConfig) ?? new AppConfig();
        // 源生成器会把缺失的布尔字段写成 false；旧配置应默认开启录制日志。
        if (!json.Contains("\"writeRecordingLog\"", StringComparison.OrdinalIgnoreCase))
        {
            config = config with { WriteRecordingLog = true };
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
