using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RtspRecServer.Server.Recording;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

public sealed class ChannelProbeService
{
    private static readonly TimeSpan ProbeDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);
    private const int MinConcurrency = 1;
    private const int MaxConcurrency = 10;
    private const int MaxRetries = 5;

    private readonly IRecordingService _recordingService;
    private readonly ConfigurationService _configurationService;
    private readonly ILogger<ChannelProbeService> _logger;
    private readonly string _profilePath;
    private readonly ConcurrentDictionary<int, ChannelProbeResult> _results = new();
    private readonly ConcurrentDictionary<int, ChannelProbeProfile> _profiles = new();
    private readonly object _profileWriteLock = new();
    private int _profilesDirty;
    private readonly object _scanLock = new();
    private ProbeScan? _scan;
    private string _probeRevision = Guid.NewGuid().ToString("N");

    public ChannelProbeService(
        IRecordingService recordingService,
        ConfigurationService configurationService,
        ILogger<ChannelProbeService> logger)
    {
        _recordingService = recordingService;
        _configurationService = configurationService;
        _logger = logger;
        _profilePath = Path.Combine(configurationService.BasePath, "channel-profiles.json");
        LoadProfiles();
    }

    public IReadOnlyList<ChannelProbeResult> GetResults()
    {
        return _results.Values.OrderBy(result => result.ChannelId).ToList();
    }

    public IReadOnlyList<ChannelProbeResult> GetProfiles()
    {
        var channels = _configurationService.GetChannels().ToDictionary(channel => channel.Id);
        return _profiles.Values
            .Where(profile =>
                channels.TryGetValue(profile.Result.ChannelId, out var channel) && channel.Url == profile.Url)
            .Select(profile => profile.Result)
            .OrderBy(result => result.ChannelId)
            .ToList();
    }

    public void InvalidateChannel(int channelId)
    {
        InvalidateChannels([channelId]);
    }

    public void InvalidateChannels(IEnumerable<int> channelIds)
    {
        var removed = false;
        lock (_scanLock)
        {
            foreach (var channelId in channelIds)
            {
                _scan?.InvalidatedIds.Add(channelId);
                removed |= _profiles.TryRemove(channelId, out _);
                if (_results.TryRemove(channelId, out _))
                {
                    _probeRevision = Guid.NewGuid().ToString("N");
                }
            }
        }

        if (removed)
        {
            SaveProfiles();
        }
    }

    public ChannelDiagnosticsSnapshot GetSnapshot(string? probeRevision = null)
    {
        lock (_scanLock)
        {
            return new ChannelDiagnosticsSnapshot
            {
                Scan = _scan is { } scan ? new ChannelProbeScan
                {
                    Id = scan.Id,
                    Total = scan.Total,
                    Done = scan.Done,
                    Stopping = scan.Cancellation.IsCancellationRequested,
                    ActiveChannelIds = scan.ActiveIds.ToArray()
                } : null,
                ProbeRevision = _probeRevision,
                Probes = probeRevision == _probeRevision ? null : GetResults().ToList()
            };
        }
    }

    public bool StopScan(Guid id)
    {
        lock (_scanLock)
        {
            if (_scan is not { } scan || scan.Id != id)
            {
                return false;
            }

            scan.Cancellation.Cancel();
            return true;
        }
    }

    public Task<IReadOnlyList<ChannelProbeResult>> ProbeAsync(
        IReadOnlyCollection<int> channelIds,
        int concurrency,
        int retries,
        CancellationToken cancellationToken) =>
        TryStartScan(channelIds, concurrency, retries, cancellationToken)
        ?? throw new InvalidOperationException("已有频道检测正在进行");

    public Task<IReadOnlyList<ChannelProbeResult>>? TryStartScan(
        IReadOnlyCollection<int> channelIds,
        int concurrency,
        int retries,
        CancellationToken cancellationToken)
    {
        lock (_scanLock)
        {
            if (_scan is not null)
            {
                return null;
            }

            var ids = channelIds.ToHashSet();
            var requested = _configurationService.GetChannels()
                .Where(channel => ids.Count == 0 || ids.Contains(channel.Id))
                .ToList();
            if (requested.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<ChannelProbeResult>>([]);
            }

            var scan = new ProbeScan(Guid.NewGuid(), requested.Count, cancellationToken);
            _scan = scan;
            return Task.Run(() => RunScanAsync(
                scan, requested, Math.Clamp(concurrency, MinConcurrency, MaxConcurrency), Math.Clamp(retries, 0, MaxRetries)));
        }
    }

    private async Task<IReadOnlyList<ChannelProbeResult>> RunScanAsync(
        ProbeScan scan, List<ChannelConfig> requested, int concurrency, int retries)
    {
        var results = new Dictionary<int, ChannelProbeResult>();
        try
        {
            await Parallel.ForEachAsync(requested, new ParallelOptions
            {
                CancellationToken = scan.Cancellation.Token,
                MaxDegreeOfParallelism = concurrency
            }, async (channel, token) =>
            {
                lock (_scanLock)
                {
                    if (scan.InvalidatedIds.Contains(channel.Id))
                    {
                        scan.Done++;
                        return;
                    }

                    scan.ActiveIds.Add(channel.Id);
                }

                ChannelProbeResult? result = null;
                try
                {
                    result = await ProbeChannelAsync(channel, retries, token);
                }
                finally
                {
                    lock (_scanLock)
                    {
                        scan.ActiveIds.Remove(channel.Id);
                        if (result is not null && !token.IsCancellationRequested)
                        {
                            scan.Done++;
                            if (!scan.InvalidatedIds.Contains(channel.Id))
                            {
                                results[channel.Id] = result;
                                StoreResult(channel, result);
                            }
                        }
                    }
                }
            });
        }
        catch (OperationCanceledException) when (scan.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "频道扫描失败");
        }
        finally
        {
            if (Interlocked.Exchange(ref _profilesDirty, 0) == 1)
            {
                SaveProfiles();
            }

            lock (_scanLock)
            {
                _scan = null;
                scan.Cancellation.Dispose();
            }
        }

        return requested.Where(channel => results.ContainsKey(channel.Id)).Select(channel => results[channel.Id]).ToList();
    }

    private async Task<ChannelProbeResult> ProbeChannelAsync(
        ChannelConfig channel,
        int retries,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.StartNew();
        ChannelProbeResult? result = null;
        var attempts = 0;
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }

            attempts++;
            result = await ProbeOnceAsync(channel, cancellationToken);
            if (result.Accessible)
            {
                break;
            }
        }

        return result! with
        {
            Attempts = attempts,
            ElapsedSeconds = Math.Round(startedAt.Elapsed.TotalSeconds, 1)
        };
    }

    private void StoreResult(ChannelConfig channel, ChannelProbeResult result)
    {
        _results[channel.Id] = result;
        _probeRevision = Guid.NewGuid().ToString("N");
        if (result.Accessible && (result.VideoTracks.Length > 0 || result.AudioTracks.Length > 0))
        {
            _profiles[channel.Id] = new ChannelProbeProfile { Url = channel.Url, Result = result };
            Interlocked.Exchange(ref _profilesDirty, 1);
        }
    }

    private async Task<ChannelProbeResult> ProbeOnceAsync(
        ChannelConfig channel,
        CancellationToken cancellationToken)
    {
        var checkedAt = DateTimeOffset.Now;
        var startedAt = Stopwatch.StartNew();
        var isLive = LiveSourceClassifier.IsLive(channel.Url);
        var config = _configurationService.GetAppConfig();
        var recordingTask = new RecordingTask
        {
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            Url = channel.Url,
            TaskName = $"Probe_{channel.Id}_{Guid.NewGuid():N}",
            DisplayName = channel.Name,
            IsLive = isLive,
            StartTime = DateTimeOffset.Now,
            EndTime = DateTimeOffset.Now + ProbeDuration,
            CreatedAt = DateTimeOffset.Now,
            Status = RecordingStatus.Recording
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeDuration);

        try
        {
            var recording = await _recordingService.RecordAsync(
                recordingTask,
                outputPath: string.Empty,
                targetDuration: null,
                recordingTransport: config.RecordingTransport,
                onProgress: _ => { },
                cancellationToken: timeout.Token,
                output: Stream.Null);

            return MapResult(channel.Id, recording, checkedAt, startedAt.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateOfflineResult(channel.Id, checkedAt, startedAt.Elapsed, "检测超时");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CreateOfflineResult(channel.Id, checkedAt, startedAt.Elapsed, ex.Message);
        }
    }

    private void LoadProfiles()
    {
        try
        {
            if (!File.Exists(_profilePath))
            {
                return;
            }

            var cache = JsonSerializer.Deserialize(
                File.ReadAllText(_profilePath),
                RtspJsonContext.Default.ChannelProbeProfileCache);
            if (cache is null || cache.Entries.Length == 0)
            {
                return;
            }

            var channels = _configurationService.GetChannels().ToDictionary(channel => channel.Id);
            foreach (var entry in cache.Entries)
            {
                if (!entry.Result.Accessible || string.IsNullOrWhiteSpace(entry.Url))
                {
                    continue;
                }
                if (!channels.TryGetValue(entry.Result.ChannelId, out var channel) || channel.Url != entry.Url)
                {
                    continue;
                }

                _profiles[entry.Result.ChannelId] = entry;
                _results[entry.Result.ChannelId] = entry.Result;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载频道检测档案失败");
        }
    }

    private void SaveProfiles()
    {
        try
        {
            lock (_profileWriteLock)
            {
                var channels = _configurationService.GetChannels().ToDictionary(channel => channel.Id);
                var entries = _profiles
                    .Where(pair => channels.TryGetValue(pair.Key, out var channel) && channel.Url == pair.Value.Url)
                    .OrderBy(pair => pair.Key)
                    .Select(pair => pair.Value)
                    .ToArray();

                var validIds = entries.Select(entry => entry.Result.ChannelId).ToHashSet();
                foreach (var channelId in _profiles.Keys)
                {
                    if (!validIds.Contains(channelId))
                    {
                        _profiles.TryRemove(channelId, out _);
                    }
                }

                var json = JsonSerializer.Serialize(
                    new ChannelProbeProfileCache { Entries = entries },
                    RtspJsonContext.Default.ChannelProbeProfileCache);

                var tempPath = _profilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _profilePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存频道检测档案失败");
        }
    }

    private static ChannelProbeResult MapResult(
        int channelId,
        RecordingResult recording,
        DateTimeOffset checkedAt,
        TimeSpan elapsed)
    {
        var tsCheck = recording.TsCheck;
        var accessible = recording.BytesWritten > 0 && tsCheck is { PacketCount: > 0 };
        var bitrate = tsCheck?.BitrateKbps is > 0
            ? tsCheck.BitrateKbps
            : accessible && elapsed.TotalSeconds > 0
                ? recording.BytesWritten * 8 / elapsed.TotalSeconds / 1000
                : (double?)null;

        return new ChannelProbeResult
        {
            ChannelId = channelId,
            Accessible = accessible,
            VideoTracks = tsCheck?.VideoTracks ?? [],
            AudioTracks = tsCheck?.AudioTracks ?? [],
            BitrateKbps = bitrate,
            TsVerdict = tsCheck?.Verdict,
            ErrorCount = tsCheck?.ErrorCount ?? 0,
            WarningCount = tsCheck?.WarningCount ?? 0,
            ErrorMessage = accessible
                ? null
                : recording.ErrorMessage ?? "未收到有效 MPEG-TS 数据",
            CheckedAt = checkedAt,
            ElapsedSeconds = Math.Round(elapsed.TotalSeconds, 1)
        };
    }

    private static ChannelProbeResult CreateOfflineResult(
        int channelId,
        DateTimeOffset checkedAt,
        TimeSpan elapsed,
        string errorMessage)
    {
        return new ChannelProbeResult
        {
            ChannelId = channelId,
            Accessible = false,
            ErrorMessage = errorMessage,
            CheckedAt = checkedAt,
            ElapsedSeconds = Math.Round(elapsed.TotalSeconds, 1)
        };
    }
    private sealed class ProbeScan(Guid id, int total, CancellationToken cancellationToken)
    {
        public Guid Id { get; } = id;
        public int Total { get; } = total;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        public HashSet<int> ActiveIds { get; } = [];
        public HashSet<int> InvalidatedIds { get; } = [];
        public int Done { get; set; }
    }
}
