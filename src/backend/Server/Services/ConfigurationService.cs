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
    {
        _basePath = AppContext.BaseDirectory;
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
            return JsonSerializer.Deserialize(json, RtspJsonContext.Default.AppConfig) ?? new AppConfig();
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
                ? JsonSerializer.Deserialize(File.ReadAllText(_appConfigPath), RtspJsonContext.Default.AppConfig) ?? new AppConfig()
                : new AppConfig();

            var transport = string.IsNullOrWhiteSpace(request.RecordingTransport)
                ? current.RecordingTransport
                : request.RecordingTransport;
            var updated = current with
            {
                MaxRecordingTasks = request.MaxRecordingTasks,
                RecordingTransport = transport
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
