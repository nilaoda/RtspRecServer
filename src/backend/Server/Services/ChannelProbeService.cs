using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly ConcurrentDictionary<int, ChannelProbeResult> _results = new();

    public ChannelProbeService(
        IRecordingService recordingService,
        ConfigurationService configurationService)
    {
        _recordingService = recordingService;
        _configurationService = configurationService;
    }

    public IReadOnlyList<ChannelProbeResult> GetResults()
    {
        return _results.Values.OrderBy(result => result.ChannelId).ToList();
    }

    public async Task<IReadOnlyList<ChannelProbeResult>> ProbeAsync(
        IReadOnlyCollection<int> channelIds,
        int concurrency,
        int retries,
        CancellationToken cancellationToken)
    {
        var channels = _configurationService.GetChannels();
        var requested = channelIds.Count == 0
            ? channels.ToList()
            : channels.Where(channel => channelIds.Contains(channel.Id)).ToList();

        if (requested.Count == 0)
        {
            return [];
        }

        var effectiveConcurrency = Math.Clamp(concurrency, MinConcurrency, MaxConcurrency);
        var effectiveRetries = Math.Clamp(retries, 0, MaxRetries);
        var results = new ChannelProbeResult[requested.Count];
        using var semaphore = new SemaphoreSlim(effectiveConcurrency);
        var tasks = requested.Select(async (channel, index) =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                results[index] = await ProbeChannelAsync(channel, effectiveRetries, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
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

        var final = result! with
        {
            Attempts = attempts,
            ElapsedSeconds = Math.Round(startedAt.Elapsed.TotalSeconds, 1)
        };
        _results[channel.Id] = final;
        return final;
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
                ? recording.BytesWritten * 8 / elapsed.TotalSeconds / 1024
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
}
