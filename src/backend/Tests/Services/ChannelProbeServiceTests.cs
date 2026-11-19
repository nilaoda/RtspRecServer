using Microsoft.Extensions.Logging.Abstractions;
using RtspRecServer.Server.Recording;
using RtspRecServer.Server.Services;
using RtspRecServer.Shared;

namespace RtspRecServer.Tests.Services;

public class ChannelProbeServiceTests
{
    [Fact]
    public async Task ScanDiscardsResultOfAnAddressThatChangedDuringRecording()
    {
        var directory = CreateTempDirectory();
        try
        {
            var configuration = new ConfigurationService(directory);
            var channel = configuration.AddChannel(new ChannelConfig { Name = "Test", Url = "udp://239.1.1.1:1234" });
            var recording = new WaitingRecordingService();
            var service = CreateService(recording, configuration);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var scan = service.ProbeAsync([channel.Id], 1, 0, deadline.Token);
            await recording.Started.Task.WaitAsync(deadline.Token);
            configuration.UpdateChannel(channel.Id, channel with { Url = "udp://239.1.1.2:1234" });
            service.InvalidateChannel(channel.Id);
            recording.Completion.SetResult(new RecordingResult
            {
                Success = true,
                BytesWritten = 1880,
                TsCheck = new TsCheckSummary { PacketCount = 10 }
            });

            Assert.Empty(await scan.WaitAsync(deadline.Token));
            Assert.Empty(service.GetResults());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ActiveScanIsVisibleAndCanBeStoppedFromAnotherRequest()
    {
        var directory = CreateTempDirectory();
        try
        {
            var configuration = new ConfigurationService(directory);
            var channel = configuration.AddChannel(new ChannelConfig { Name = "Test", Url = "udp://239.1.1.1:1234" });
            var recording = new WaitingRecordingService();
            var service = CreateService(recording, configuration);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var scan = service.TryStartScan([channel.Id], 1, 0, deadline.Token);
            Assert.NotNull(scan);
            await recording.Started.Task.WaitAsync(deadline.Token);

            var snapshot = service.GetSnapshot();
            Assert.NotNull(snapshot.Scan);
            Assert.Equal([channel.Id], snapshot.Scan.ActiveChannelIds);
            Assert.Null(service.TryStartScan([channel.Id], 1, 0, deadline.Token));
            Assert.False(service.StopScan(Guid.NewGuid()));
            Assert.True(service.StopScan(snapshot.Scan.Id));

            Assert.Empty(await scan.WaitAsync(deadline.Token));
            Assert.Null(service.GetSnapshot().Scan);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ProbeAsync_RetriesUntilAccessible()
    {
        var directory = CreateTempDirectory();
        try
        {
            var configuration = new ConfigurationService(directory);
            var channel = configuration.AddChannel(new ChannelConfig
            {
                Name = "CCTV-1",
                Url = "rtsp://example.com/1"
            });
            var recording = new FlakyRecordingService(failuresBeforeSuccess: 2);
            var service = CreateService(recording, configuration);

            var results = await service.ProbeAsync([channel.Id], concurrency: 2, retries: 3, CancellationToken.None);

            var result = Assert.Single(results);
            Assert.True(result.Accessible);
            Assert.Equal(3, result.Attempts);
            Assert.Equal(3, recording.Calls);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ProbeAsync_DoesNotRetryWhenAccessible()
    {
        var directory = CreateTempDirectory();
        try
        {
            var configuration = new ConfigurationService(directory);
            var channel = configuration.AddChannel(new ChannelConfig
            {
                Name = "CCTV-1",
                Url = "rtsp://example.com/1"
            });
            var recording = new FlakyRecordingService(failuresBeforeSuccess: 0);
            var service = CreateService(recording, configuration);

            var results = await service.ProbeAsync([channel.Id], concurrency: 3, retries: 5, CancellationToken.None);

            var result = Assert.Single(results);
            Assert.True(result.Accessible);
            Assert.Equal(1, result.Attempts);
            Assert.Equal(1, recording.Calls);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ProbeAsync_ReportsAttemptsWhenRetriesExhausted()
    {
        var directory = CreateTempDirectory();
        try
        {
            var configuration = new ConfigurationService(directory);
            var channel = configuration.AddChannel(new ChannelConfig
            {
                Name = "CCTV-1",
                Url = "rtsp://example.com/1"
            });
            var recording = new FlakyRecordingService(failuresBeforeSuccess: 10);
            var service = CreateService(recording, configuration);

            var results = await service.ProbeAsync([channel.Id], concurrency: 1, retries: 1, CancellationToken.None);

            var result = Assert.Single(results);
            Assert.False(result.Accessible);
            Assert.Equal(2, result.Attempts);
            Assert.Equal(2, recording.Calls);
            Assert.NotNull(result.ErrorMessage);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ProbeAsync_PersistsProfileAcrossRestart()
    {
        var directory = CreateTempDirectory();
        try
        {
            var configuration = new ConfigurationService(directory);
            var channel = configuration.AddChannel(new ChannelConfig
            {
                Name = "CCTV-1",
                Url = "rtsp://example.com/1"
            });
            var service = CreateService(new FlakyRecordingService(failuresBeforeSuccess: 0), configuration);

            await service.ProbeAsync([channel.Id], concurrency: 1, retries: 0, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(directory, "channel-profiles.json")));

            var reloaded = CreateService(new FlakyRecordingService(failuresBeforeSuccess: 10), new ConfigurationService(directory));
            var profile = Assert.Single(reloaded.GetProfiles());
            Assert.Equal(channel.Id, profile.ChannelId);
            Assert.True(profile.Accessible);
            Assert.Equal("H.264", Assert.Single(profile.VideoTracks).Label);
            Assert.Contains(reloaded.GetResults(), result => result.ChannelId == channel.Id && result.Accessible);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ProbeFailure_KeepsLastSuccessfulProfile()
    {
        var directory = CreateTempDirectory();
        try
        {
            var configuration = new ConfigurationService(directory);
            var channel = configuration.AddChannel(new ChannelConfig
            {
                Name = "CCTV-1",
                Url = "rtsp://example.com/1"
            });
            var service = CreateService(new FlakyRecordingService(failuresBeforeSuccess: 0), configuration);
            await service.ProbeAsync([channel.Id], concurrency: 1, retries: 0, CancellationToken.None);

            var failing = CreateService(new FlakyRecordingService(failuresBeforeSuccess: 10), new ConfigurationService(directory));
            var results = await failing.ProbeAsync([channel.Id], concurrency: 1, retries: 0, CancellationToken.None);

            Assert.False(Assert.Single(results).Accessible);
            var profile = Assert.Single(failing.GetProfiles());
            Assert.True(profile.Accessible);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task InvalidateChannel_RemovesPersistedProfile()
    {
        var directory = CreateTempDirectory();
        try
        {
            var configuration = new ConfigurationService(directory);
            var channel = configuration.AddChannel(new ChannelConfig
            {
                Name = "CCTV-1",
                Url = "rtsp://example.com/1"
            });
            var service = CreateService(new FlakyRecordingService(failuresBeforeSuccess: 0), configuration);
            await service.ProbeAsync([channel.Id], concurrency: 1, retries: 0, CancellationToken.None);

            service.InvalidateChannel(channel.Id);

            Assert.Empty(service.GetProfiles());
            var reloaded = CreateService(new FlakyRecordingService(failuresBeforeSuccess: 0), new ConfigurationService(directory));
            Assert.Empty(reloaded.GetProfiles());
            Assert.Empty(reloaded.GetResults());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task GetProfiles_DropsProfileAfterUrlChange()
    {
        var directory = CreateTempDirectory();
        try
        {
            var configuration = new ConfigurationService(directory);
            var channel = configuration.AddChannel(new ChannelConfig
            {
                Name = "CCTV-1",
                Url = "rtsp://example.com/1"
            });
            var service = CreateService(new FlakyRecordingService(failuresBeforeSuccess: 0), configuration);
            await service.ProbeAsync([channel.Id], concurrency: 1, retries: 0, CancellationToken.None);

            new ConfigurationService(directory).UpdateChannel(channel.Id, channel with
            {
                Url = "rtsp://example.com/2"
            });

            Assert.Empty(service.GetProfiles());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static ChannelProbeService CreateService(
        IRecordingService recording,
        ConfigurationService configuration)
    {
        return new ChannelProbeService(recording, configuration, NullLogger<ChannelProbeService>.Instance);
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rtsp-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class FlakyRecordingService : IRecordingService
    {
        private readonly int _failuresBeforeSuccess;

        public FlakyRecordingService(int failuresBeforeSuccess)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public int Calls { get; private set; }

        public Task<RecordingResult> RecordAsync(
            RecordingTask task,
            string outputPath,
            TimeSpan? targetDuration,
            string recordingTransport,
            Action<RecordingProgress> onProgress,
            CancellationToken cancellationToken,
            Stream? output = null)
        {
            Calls++;
            var succeeded = Calls > _failuresBeforeSuccess;
            return Task.FromResult(new RecordingResult
            {
                Success = succeeded,
                OutputPath = outputPath,
                BytesWritten = succeeded ? 1880 : 0,
                ErrorMessage = succeeded ? null : "连接失败",
                TsCheck = succeeded
                    ? new TsCheckSummary
                    {
                        Verdict = "pass",
                        PacketCount = 10,
                        BytesScanned = 1880,
                        BitrateKbps = 1000,
                        VideoTracks = [new TsStreamTrack { Pid = 256, Label = "H.264" }],
                        AudioTracks = [new TsStreamTrack { Pid = 257, Label = "AAC" }]
                    }
                    : null
            });
        }
    }

    private sealed class WaitingRecordingService : IRecordingService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<RecordingResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RecordingResult> RecordAsync(
            RecordingTask task, string outputPath, TimeSpan? targetDuration, string recordingTransport,
            Action<RecordingProgress> onProgress, CancellationToken cancellationToken, Stream? output = null)
        {
            Started.SetResult();
            return await Completion.Task.WaitAsync(cancellationToken);
        }
    }
}
