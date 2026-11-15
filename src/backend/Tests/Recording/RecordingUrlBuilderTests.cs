using RtspRecServer.Server.Services;
using RtspRecServer.Shared;

namespace RtspRecServer.Tests.Recording;

public class RecordingUrlBuilderTests
{
    [Fact]
    public void Build_PreservesExistingPlayseek()
    {
        var task = CreateTask("rtsp://host/live?playseek=20260102120000-20260102130000");

        Assert.Equal(task.Url, RecordingUrlBuilder.Build(task));
    }

    [Fact]
    public void Build_PreservesExistingPlayseek_CaseInsensitive()
    {
        var task = CreateTask("rtsp://host/live?PLAYSEEK=abc");

        Assert.Equal(task.Url, RecordingUrlBuilder.Build(task));
    }

    [Fact]
    public void Build_PreservesExistingStartTime()
    {
        var task = CreateTask("rtsp://host/live?starttime=20260102T120000Z");

        Assert.Equal(task.Url, RecordingUrlBuilder.Build(task));
    }

    [Fact]
    public void Build_Pltv_AppendsLocalPlayseekWithOneMinutePadding()
    {
        var start = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));
        var end = start.AddMinutes(30);
        var task = CreateTask("rtsp://host/PLTV/88888888/1.smil", start, end);
        var expectedStart = start.ToLocalTime().ToString("yyyyMMddHHmmss");
        var expectedEnd = end.ToLocalTime().AddMinutes(1).ToString("yyyyMMddHHmmss");

        Assert.Equal($"rtsp://host/PLTV/88888888/1.smil?playseek={expectedStart}-{expectedEnd}", RecordingUrlBuilder.Build(task));
    }

    [Fact]
    public void Build_Pltv_UsesAmpersandWhenQueryExists()
    {
        var start = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));
        var end = start.AddMinutes(30);
        var task = CreateTask("rtsp://host/PLTV/1.smil?token=abc", start, end);

        var url = RecordingUrlBuilder.Build(task);

        Assert.Contains("?token=abc&playseek=", url);
    }

    [Fact]
    public void Build_Pltv_IsCaseInsensitive()
    {
        var task = CreateTask("rtsp://host/pltv/1.smil");

        Assert.Contains("playseek=", RecordingUrlBuilder.Build(task));
    }

    [Fact]
    public void Build_Generic_UsesUtcStartAndEndWithOneMinutePadding()
    {
        var start = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));
        var end = start.AddMinutes(30);
        var task = CreateTask("rtsp://host/live", start, end);

        Assert.Equal(
            "rtsp://host/live?starttime=20260101T190405Z&endtime=20260101T193505Z",
            RecordingUrlBuilder.Build(task));
    }

    [Fact]
    public void Build_Generic_UsesAmpersandWhenQueryExists()
    {
        var start = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));
        var end = start.AddMinutes(30);
        var task = CreateTask("rtsp://host/live?token=abc", start, end);

        Assert.Equal(
            "rtsp://host/live?token=abc&starttime=20260101T190405Z&endtime=20260101T193505Z",
            RecordingUrlBuilder.Build(task));
    }

    private static RecordingTask CreateTask(
        string url,
        DateTimeOffset? start = null,
        DateTimeOffset? end = null)
    {
        var startTime = start ?? new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));
        return new RecordingTask
        {
            Url = url,
            StartTime = startTime,
            EndTime = end ?? startTime.AddMinutes(30)
        };
    }
}
