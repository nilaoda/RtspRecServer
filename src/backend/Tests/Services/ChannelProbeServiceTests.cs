using RtspRecServer.Server.Recording;
using RtspRecServer.Server.Services;
using RtspRecServer.Shared;

namespace RtspRecServer.Tests.Services;

public class ChannelProbeServiceTests
{
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
            var service = new ChannelProbeService(recording, configuration);

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
            var service = new ChannelProbeService(recording, configuration);

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
            var service = new ChannelProbeService(recording, configuration);

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
                        BitrateKbps = 1000
                    }
                    : null
            });
        }
    }
}
