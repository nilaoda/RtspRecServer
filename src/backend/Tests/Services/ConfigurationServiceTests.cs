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

    [Fact]
    public void UpdateAppConfig_AppliesAllFieldsAndKeepsPasswordWhenEmpty()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new ConfigurationService(directory);

            var updated = service.UpdateAppConfig(new AppConfigUpdateRequest
            {
                Port = 9090,
                RecordPath = " D:/records ",
                Host = ["0.0.0.0", " 192.0.2.10 "],
                UseAuth = true,
                Username = " viewer ",
                Password = null,
                MaxRecordingTasks = 4,
                RecordingTransport = "MP2T/RTP/TCP",
                WriteRecordingLog = false,
                TaskErrorLogMaxEntries = 5000,
                Epg = new EpgConfigUpdate
                {
                    Url = " http://epg.example.com/e.xml.gz ",
                    UpdateTime = "06:30",
                    CacheDurationHours = 6
                }
            });

            Assert.Equal(9090, updated.Port);
            Assert.Equal("D:/records", updated.RecordPath);
            Assert.Equal(["0.0.0.0", "192.0.2.10"], updated.Host);
            Assert.True(updated.UseAuth);
            Assert.Equal("viewer", updated.Username);
            Assert.Equal("admin", updated.Password); // 空密码保持原值
            Assert.Equal(4, updated.MaxRecordingTasks);
            Assert.Equal("MP2T/RTP/TCP", updated.RecordingTransport);
            Assert.False(updated.WriteRecordingLog);
            Assert.Equal(5000, updated.TaskErrorLogMaxEntries);
            Assert.Equal("http://epg.example.com/e.xml.gz", updated.Epg.Url);
            Assert.Equal("06:30", updated.Epg.UpdateTime);
            Assert.Equal(6, updated.Epg.CacheDurationHours);

            var reloaded = new ConfigurationService(directory).GetAppConfig();
            Assert.Equal(9090, reloaded.Port);
            Assert.Equal("viewer", reloaded.Username);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void UpdateAppConfig_ReportsOnlyStartupBoundFieldsAsRestartRequired()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new ConfigurationService(directory);

            var view = service.GetAppConfigView();
            Assert.Empty(view.RestartRequiredFields);

            service.UpdateAppConfig(new AppConfigUpdateRequest { Port = 9090, RecordPath = "./other" });
            view = service.GetAppConfigView();
            Assert.Equal(["port"], view.RestartRequiredFields);

            service.UpdateAppConfig(new AppConfigUpdateRequest { Port = 8080, Host = ["127.0.0.1"] });
            view = service.GetAppConfigView();
            Assert.Equal(["host"], view.RestartRequiredFields);

            service.UpdateAppConfig(new AppConfigUpdateRequest { Host = ["0.0.0.0"] });
            view = service.GetAppConfigView();
            Assert.Empty(view.RestartRequiredFields);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void UpdateAppConfig_RejectsInvalidPort(int port)
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new ConfigurationService(directory);
            Assert.Throws<InvalidOperationException>(
                () => service.UpdateAppConfig(new AppConfigUpdateRequest { Port = port }));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void UpdateAppConfig_RejectsInvalidTransportHostAndUpdateTime()
    {
        var directory = CreateTempDirectory();
        try
        {
            var service = new ConfigurationService(directory);

            Assert.Throws<InvalidOperationException>(
                () => service.UpdateAppConfig(new AppConfigUpdateRequest { RecordingTransport = "UDP" }));
            Assert.Throws<InvalidOperationException>(
                () => service.UpdateAppConfig(new AppConfigUpdateRequest { Host = [" "] }));
            Assert.Throws<InvalidOperationException>(
                () => service.UpdateAppConfig(new AppConfigUpdateRequest
                {
                    Epg = new EpgConfigUpdate { UpdateTime = "8:00" }
                }));
            Assert.Throws<InvalidOperationException>(
                () => service.UpdateAppConfig(new AppConfigUpdateRequest
                {
                    Epg = new EpgConfigUpdate { CacheDurationHours = 0 }
                }));
            Assert.Throws<InvalidOperationException>(
                () => service.UpdateAppConfig(new AppConfigUpdateRequest { TaskErrorLogMaxEntries = 10 }));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void GetAppConfig_NormalizesLegacyMissingFields()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "recsettings.json"), """
            {
              "port": 8080,
              "recordPath": "./records",
              "host": ["0.0.0.0"],
              "useAuth": false,
              "username": "admin",
              "password": "admin",
              "maxRecordingTasks": 1,
              "epg": { "url": "http://epg.example.com/e.xml" }
            }
            """);

            var config = new ConfigurationService(directory).GetAppConfig();

            Assert.Equal("MP2T/TCP", config.RecordingTransport);
            Assert.True(config.WriteRecordingLog);
            Assert.Equal(10_000, config.TaskErrorLogMaxEntries);
            Assert.Equal("08:00", config.Epg.UpdateTime);
            Assert.Equal(24, config.Epg.CacheDurationHours);
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
