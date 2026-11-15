using RtspRecServer.Server.Recording;

namespace RtspRecServer.Tests.Recording;

public class LiveSourceClassifierTests
{
    [Theory]
    [InlineData("http://udpxy.example.com:5140/rtp/239.1.1.1:8000")]
    [InlineData("http://udpxy.example.com:5140/udp/239.1.1.1:8000")]
    [InlineData("https://host/rtp/239.1.1.1:8000")]
    [InlineData("rtp://239.1.1.1:8000")]
    [InlineData("rtp://@239.1.1.1:8000")]
    [InlineData("udp://@239.1.1.1:8000")]
    public void IsLive_ReturnsTrueForLiveSources(string url)
    {
        Assert.True(LiveSourceClassifier.IsLive(url));
    }

    [Theory]
    [InlineData("rtsp://host/PLTV/1.smil")]
    [InlineData("http://host/live.m3u8")]
    [InlineData("http://host/path?next=/rtp/239.1.1.1:8000")]
    [InlineData("")]
    public void IsLive_ReturnsFalseForNonLiveSources(string url)
    {
        Assert.False(LiveSourceClassifier.IsLive(url));
    }

    [Theory]
    [InlineData("rtp://239.1.1.1:8000", true)]
    [InlineData("udp://@239.1.1.1:8000", true)]
    [InlineData("http://host/rtp/239.1.1.1:8000", false)]
    [InlineData("rtsp://host/live", false)]
    public void IsDirectLive_RequiresRtpOrUdpScheme(string url, bool expected)
    {
        Assert.Equal(expected, LiveSourceClassifier.IsDirectLive(url));
    }

    [Theory]
    [InlineData("rtp://239.1.1.1:8000", "MP2T/TCP", "RTP/UDP")]
    [InlineData("udp://@239.1.1.1:8000", "MP2T/TCP", "UDP")]
    [InlineData("http://host/rtp/239.1.1.1:8000", "MP2T/TCP", "HTTP")]
    [InlineData("rtsp://host/live", "MP2T/RTP/TCP", "MP2T/RTP/TCP")]
    public void GetTransportDescription_UsesActualLiveTransport(string url, string fallback, string expected)
    {
        Assert.Equal(expected, LiveSourceClassifier.GetTransportDescription(url, fallback));
    }

    [Theory]
    [InlineData("http://host/rtp/239.1.1.1:8000", true)]
    [InlineData("https://host/udp/239.1.1.1:8000", true)]
    [InlineData("  http://host/rtp/239.1.1.1:8000  ", true)]
    [InlineData("rtp://239.1.1.1:8000", false)]
    [InlineData("rtsp://host/live", false)]
    public void IsHttpLive_RequiresHttpScheme(string url, bool expected)
    {
        Assert.Equal(expected, LiveSourceClassifier.IsHttpLive(url));
    }
}
