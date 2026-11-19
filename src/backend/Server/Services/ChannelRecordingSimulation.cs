using System.Diagnostics;
using RtspRecServer.Server.Recording;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

internal sealed class ChannelRecordingSimulation(
    IRecordingService recordingService,
    ConfigurationService configuration,
    IHostApplicationLifetime lifetime,
    ILogger<ChannelRecordingSimulation> logger)
{
    private readonly object _sync = new();
    private readonly Dictionary<int, SimulationRun> _runs = [];

    public List<ChannelRecordingSimulationUpdate> GetUpdates()
    {
        lock (_sync)
        {
            return _runs.Values.Select(run => run.Active
                ? run.Update with { ElapsedSeconds = run.Clock.Elapsed.TotalSeconds }
                : run.Update).ToList();
        }
    }

    public IResult Start(int channelId, int durationSeconds)
    {
        if (durationSeconds is < 5 or > 300)
        {
            return Results.BadRequest(ApiResponse.Create("模拟录制时长须为 5 至 300 秒"));
        }

        lock (_sync)
        {
            var channel = configuration.GetChannels().FirstOrDefault(item => item.Id == channelId);
            if (channel is null)
            {
                return Results.NotFound(ApiResponse.Create("频道不存在"));
            }

            if (_runs.TryGetValue(channelId, out var current) && current.Active)
            {
                return Results.Conflict(ApiResponse.Create("该频道正在模拟录制"));
            }

            var transport = configuration.GetAppConfig().RecordingTransport;
            var run = new SimulationRun(Guid.NewGuid(), channel.Id, durationSeconds, lifetime.ApplicationStopping);
            _runs[channelId] = run;
            _ = Task.Run(() => RunAsync(run, channel, transport));
            return Results.Accepted();
        }
    }

    public bool Stop(int channelId, Guid id)
    {
        lock (_sync)
        {
            if (!_runs.TryGetValue(channelId, out var run) || run.Update.Id != id || !run.Active)
            {
                return false;
            }

            run.Update = run.Update with { Status = "stopping" };
            run.Cancellation.Cancel();
            return true;
        }
    }

    public void InvalidateChannels(IEnumerable<int> ids)
    {
        lock (_sync)
        {
            foreach (var id in ids)
            {
                if (_runs.Remove(id, out var run) && run.Active)
                {
                    run.Cancellation.Cancel();
                }
            }
        }
    }

    private async Task RunAsync(SimulationRun run, ChannelConfig channel, string transport)
    {
        var duration = TimeSpan.FromSeconds(run.Update.DurationSeconds);
        var isLive = LiveSourceClassifier.IsLive(channel.Url);
        var now = DateTimeOffset.UtcNow;
        // RTSP 使用已经生成的回放片段，避开服务端的时移延迟。
        var task = new RecordingTask
        {
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            TaskName = $"模拟录制 · {channel.Name}",
            Url = channel.Url,
            IsLive = isLive,
            StartTime = isLive ? now : now.AddMinutes(-2) - duration,
            EndTime = isLive ? now + duration : now.AddMinutes(-2)
        };
        RecordingResult result;
        try
        {
            if (!isLive)
            {
                task = task with { Url = RecordingUrlBuilder.Build(task) };
            }

            result = await recordingService.RecordAsync(
                task, string.Empty, isLive ? null : duration, transport,
                progress =>
                {
                    lock (_sync)
                    {
                        run.Update = run.Update with
                        {
                            BytesReceived = progress.BytesWritten,
                            TsCheck = progress.TsCheck ?? run.Update.TsCheck
                        };
                    }
                },
                run.Cancellation.Token,
                output: Stream.Null);
        }
        catch (Exception ex)
        {
            if (!run.Cancellation.IsCancellationRequested)
            {
                logger.LogError(ex, "频道 {ChannelId} 模拟录制失败", channel.Id);
            }

            result = new RecordingResult
            {
                ErrorMessage = ex is OperationCanceledException && run.Cancellation.IsCancellationRequested ? null : ex.Message
            };
        }

        lock (_sync)
        {
            var tsCheck = result.TsCheck ?? run.Update.TsCheck;
            var timedOut = run.Cancellation.IsCancellationRequested;
            var stopped = run.Update.Status == "stopping";
            var completed = tsCheck is { PacketCount: > 0 } && (result.Success || timedOut && result.ErrorMessage is null);
            run.Update = run.Update with
            {
                Status = stopped ? "stopped" : completed ? "completed" : "failed",
                BytesReceived = Math.Max(result.BytesWritten, run.Update.BytesReceived),
                TsCheck = tsCheck,
                ElapsedSeconds = run.Clock.Elapsed.TotalSeconds,
                ErrorMessage = stopped || completed ? null : result.ErrorMessage ?? "未收到有效的 MPEG-TS 数据"
            };
            run.Cancellation.Dispose();
        }
    }

    private sealed class SimulationRun
    {
        public SimulationRun(Guid id, int channelId, int durationSeconds, CancellationToken stopping)
        {
            Update = new ChannelRecordingSimulationUpdate { Id = id, ChannelId = channelId, DurationSeconds = durationSeconds };
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            Cancellation.CancelAfter(TimeSpan.FromSeconds(durationSeconds));
        }

        public ChannelRecordingSimulationUpdate Update { get; set; }
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public CancellationTokenSource Cancellation { get; }
        public bool Active => Update.Status is "running" or "stopping";
    }
}
