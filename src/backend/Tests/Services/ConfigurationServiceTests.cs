using RtspRecServer.Server.Services;
using RtspRecServer.Shared;

namespace RtspRecServer.Tests.Services;

public class ConfigurationServiceTests
{
    [Fact]
    public void AddChannels_AddsAllWithSequentialIds()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new ConfigurationService(directory);
            var first = service.AddChannel(new ChannelConfig
            {
                Name = "CCTV-1",
                Url = "rtsp://example.com/1"
            });

            var result = service.AddChannels(new List<ChannelImportItem>
            {
                new() { Name = " CCTV-2 ", Url = " http://udpxy.example.com:5140/rtp/239.1.1.2:8000 " },
                new() { Name = "CCTV-3", Url = "http://udpxy.example.com:5140/rtp/239.1.1.3:8000" }
            });

            Assert.Equal(2, result.AddedCount);
            Assert.Equal(2, result.Channels.Count);
            Assert.Equal(first.Id + 1, result.Channels[0].Id);
            Assert.Equal(first.Id + 2, result.Channels[1].Id);
            Assert.Equal("CCTV-2", result.Channels[0].Name);
            Assert.Equal("http://udpxy.example.com:5140/rtp/239.1.1.2:8000", result.Channels[0].Url);

            var reloaded = new ConfigurationService(directory).GetChannels();
            Assert.Equal(3, reloaded.Count);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void GetAppConfig_MissingWriteRecordingLog_DefaultsToTrue()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "recsettings.json"), "{\"maxRecordingTasks\":2}");
            var service = new ConfigurationService(directory);

            Assert.True(service.GetAppConfig().WriteRecordingLog);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void UpdateAppConfig_PersistsRecordingLogSwitch()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new ConfigurationService(directory);
            var updated = service.UpdateAppConfig(new AppConfigUpdateRequest
            {
                MaxRecordingTasks = 2,
                RecordingTransport = "MP2T/RTP/TCP",
                WriteRecordingLog = false
            });

            Assert.False(updated.WriteRecordingLog);
            Assert.False(new ConfigurationService(directory).GetAppConfig().WriteRecordingLog);

            var preserved = service.UpdateAppConfig(new AppConfigUpdateRequest
            {
                MaxRecordingTasks = 3,
                RecordingTransport = "MP2T/TCP"
            });
            Assert.False(preserved.WriteRecordingLog);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void AddChannels_RejectsEmptyBatch()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new ConfigurationService(directory);

            Assert.Throws<InvalidOperationException>(
                () => service.AddChannels(Array.Empty<ChannelImportItem>()));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void AddChannels_RejectsInvalidItemWithoutWriting()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new ConfigurationService(directory);
            service.AddChannel(new ChannelConfig
            {
                Name = "CCTV-1",
                Url = "rtsp://example.com/1"
            });

            Assert.Throws<InvalidOperationException>(() => service.AddChannels(new List<ChannelImportItem>
            {
                new() { Name = "CCTV-2", Url = "http://udpxy.example.com:5140/rtp/239.1.1.2:8000" },
                new() { Name = " ", Url = "http://udpxy.example.com:5140/rtp/239.1.1.3:8000" }
            }));

            Assert.Single(service.GetChannels());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DeleteChannels_DeletesExistingChannelsAndReportsMissingIds()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new ConfigurationService(directory);
            var first = service.AddChannel(new ChannelConfig { Name = "CCTV-1", Url = "rtsp://example.com/1" });
            var second = service.AddChannel(new ChannelConfig { Name = "CCTV-2", Url = "rtsp://example.com/2" });

            var result = service.DeleteChannels([first.Id, second.Id, 9999]);

            Assert.Equal(2, result.Succeeded);
            Assert.Equal(1, result.Failed);
            Assert.Equal([first.Id, second.Id], result.SucceededIds);
            Assert.Equal([9999], result.FailedIds);
            Assert.Empty(new ConfigurationService(directory).GetChannels());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rtsp-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
