using System.Text.Json;
using RtspRecServer.Shared;

namespace RtspRecServer.Tests.Infrastructure;

public class JsonContractTests
{
    [Fact]
    public void AppConfig_UsesCamelCaseAndRoundTrips()
    {
        var config = new AppConfig
        {
            Port = 9000,
            MaxRecordingTasks = 3,
            RecordingTransport = "MP2T/RTP/TCP"
        };

        var json = JsonSerializer.Serialize(config, RtspJsonContext.Default.AppConfig);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(9000, root.GetProperty("port").GetInt32());
        Assert.Equal(3, root.GetProperty("maxRecordingTasks").GetInt32());
        Assert.Equal("MP2T/RTP/TCP", root.GetProperty("recordingTransport").GetString());
        Assert.True(root.GetProperty("writeRecordingLog").GetBoolean());
        Assert.True(root.TryGetProperty("epg", out _));
        Assert.False(root.TryGetProperty("Port", out _));

        var roundTrip = JsonSerializer.Deserialize(json, RtspJsonContext.Default.AppConfig);
        Assert.NotNull(roundTrip);
        Assert.Equal(9000, roundTrip!.Port);
        Assert.Equal(3, roundTrip.MaxRecordingTasks);
        Assert.Equal("MP2T/RTP/TCP", roundTrip.RecordingTransport);
    }

    [Fact]
    public void RecordingTaskDto_SerializesStatusAsNumber()
    {
        var dto = new RecordingTaskDto
        {
            Id = 7,
            Status = RecordingStatus.Recording,
            IsLive = true,
            BytesWritten = 12345,
            TaskName = "Playback_CCTV-1",
            TsCheck = new TsCheckSummary
            {
                Verdict = "pass",
                Video = "H.264",
                Audio = "AAC",
                PidCount = 3
            }
        };

        var json = JsonSerializer.Serialize(dto, RtspJsonContext.Default.RecordingTaskDto);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(7, root.GetProperty("id").GetInt64());
        Assert.Equal(1, root.GetProperty("status").GetInt32());
        Assert.True(root.GetProperty("isLive").GetBoolean());
        Assert.Equal(12345, root.GetProperty("bytesWritten").GetInt64());
        Assert.Equal("pass", root.GetProperty("tsCheck").GetProperty("verdict").GetString());
        Assert.Equal("H.264", root.GetProperty("tsCheck").GetProperty("video").GetString());
        Assert.False(root.TryGetProperty("Status", out _));
    }

    [Fact]
    public void TaskUpdateMessage_UsesExpectedTypeAndPayload()
    {
        var message = new TaskUpdateMessage
        {
            Task = new RecordingTaskDto
            {
                Id = 7,
                Status = RecordingStatus.Completed
            }
        };

        var json = JsonSerializer.Serialize(message, RtspJsonContext.Default.TaskUpdateMessage);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("taskUpdated", root.GetProperty("type").GetString());
        Assert.Equal(7, root.GetProperty("task").GetProperty("id").GetInt64());
        Assert.Equal(2, root.GetProperty("task").GetProperty("status").GetInt32());
    }

    [Fact]
    public void ChannelConfigList_RoundTrips()
    {
        var channels = new List<ChannelConfig>
        {
            new() { Id = 1, Name = "CCTV-1", Url = "rtsp://host/1" },
            new() { Id = 2, Name = "CCTV-2", Url = "rtsp://host/2" }
        };

        var json = JsonSerializer.Serialize(channels, RtspJsonContext.Default.ListChannelConfig);
        var roundTrip = JsonSerializer.Deserialize(json, RtspJsonContext.Default.ListChannelConfig);

        Assert.NotNull(roundTrip);
        Assert.Equal(2, roundTrip!.Count);
        Assert.Equal("CCTV-1", roundTrip[0].Name);
        Assert.Equal("rtsp://host/2", roundTrip[1].Url);
    }

    [Fact]
    public void RecordingTask_RoundTripsStatus()
    {
        var task = new RecordingTask
        {
            Id = 3,
            Status = RecordingStatus.Stopped,
            StartTime = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8)),
            EndTime = new DateTimeOffset(2026, 1, 2, 4, 4, 5, TimeSpan.FromHours(8)),
            BytesWritten = 1024
        };

        var json = JsonSerializer.Serialize(task, RtspJsonContext.Default.RecordingTask);
        var roundTrip = JsonSerializer.Deserialize(json, RtspJsonContext.Default.RecordingTask);

        Assert.NotNull(roundTrip);
        Assert.Equal(RecordingStatus.Stopped, roundTrip!.Status);
        Assert.Equal(task.StartTime, roundTrip.StartTime);
        Assert.Equal(task.EndTime, roundTrip.EndTime);
        Assert.Equal(1024, roundTrip.BytesWritten);
    }
}
