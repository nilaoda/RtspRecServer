using System.Net;
using System.Net.Sockets;
using RtspRecServer.Server.Recording;
using RtspRecServer.Shared;

namespace RtspRecServer.Tests.Recording;

public class UdpLiveRecordingServiceTests
{
    [Theory]
    [InlineData("rtp://239.1.1.1:8000", true, "239.1.1.1", 8000, null)]
    [InlineData("rtp://239.1.1.1:8000/", true, "239.1.1.1", 8000, null)]
    [InlineData("udp://@239.1.1.1:9000", false, "239.1.1.1", 9000, null)]
    [InlineData("rtp://@239.1.1.1:8000?localaddr=192.0.2.10", true, "239.1.1.1", 8000, "192.0.2.10")]
    [InlineData("udp://239.1.1.1:9000?localAddress=192.0.2.10", false, "239.1.1.1", 9000, "192.0.2.10")]
    public void TryParse_ValidDirectSources(
        string url,
        bool expectedRtp,
        string expectedGroup,
        int expectedPort,
        string? expectedLocalAddress)
    {
        var success = DirectUdpSourceParser.TryParse(url, out var source, out var error);

        Assert.True(success, error);
        Assert.NotNull(source);
        Assert.Equal(expectedRtp, source!.IsRtp);
        Assert.Equal(IPAddress.Parse(expectedGroup), source.GroupAddress);
        Assert.Equal(expectedPort, source.Port);
        Assert.Equal(expectedLocalAddress is null ? null : IPAddress.Parse(expectedLocalAddress), source.LocalAddress);
    }

    [Theory]
    [InlineData("http://example.com/live.m3u8")]
    [InlineData("rtsp://example.com/live")]
    [InlineData("rtp://239.1.1.1")]
    [InlineData("rtp://239.1.1.1:70000")]
    [InlineData("rtp://example.com:8000")]
    public void TryParse_RejectsInvalidSources(string url)
    {
        Assert.False(DirectUdpSourceParser.TryParse(url, out _, out _));
    }

    [Fact]
    public async Task UdpReceiveHub_AllowsMultipleSubscriptionsOnSamePort()
    {
        var port = GetFreeUdpPort();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var hub = new UdpReceiveHub();

        await using var first = hub.Subscribe(null, null, port, capacity: 8);
        await using var second = hub.Subscribe(null, null, port, capacity: 8);

        using var sender = new UdpClient(AddressFamily.InterNetwork);
        await sender.SendAsync(new byte[] { 0x47, 1, 2 }, 3, new IPEndPoint(IPAddress.Loopback, port));

        using var firstDatagram = await first.Reader.ReadAsync(cancellation.Token);
        using var secondDatagram = await second.Reader.ReadAsync(cancellation.Token);
        Assert.Equal(3, firstDatagram.Length);
        Assert.Equal(3, secondDatagram.Length);
    }

    [Fact]
    public void ShouldDeliver_FiltersByMulticastGroupAndSupportsUnicast()
    {
        var group = IPAddress.Parse("239.1.1.1");

        Assert.True(UdpEndpointReceiver.ShouldDeliver(group, group));
        Assert.True(UdpEndpointReceiver.ShouldDeliver(group, IPAddress.Any));
        Assert.False(UdpEndpointReceiver.ShouldDeliver(group, IPAddress.Parse("239.1.1.2")));
        Assert.True(UdpEndpointReceiver.ShouldDeliver(null, IPAddress.Loopback));
        Assert.False(UdpEndpointReceiver.ShouldDeliver(null, group));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecordsRtpAndRawUdpPacketsOverLoopback(bool useRtp)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rtsp-udp-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(directory, "recording.ts");
        var port = GetFreeUdpPort();
        var scheme = useRtp ? "rtp" : "udp";
        var task = new RecordingTask
        {
            Id = 1,
            ChannelId = 1,
            ChannelName = "Test",
            TaskName = "Live_Test",
            IsLive = true,
            Url = $"{scheme}://127.0.0.1:{port}",
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow.AddMinutes(1)
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var service = new UdpLiveRecordingService();
        var recording = service.RecordAsync(
            task,
            outputPath,
            targetDuration: null,
            recordingTransport: "MP2T/TCP",
            onProgress: _ => { },
            cancellationToken: cancellation.Token);

        try
        {
            await WaitUntilAsync(() => File.Exists(outputPath), TimeSpan.FromSeconds(3));
            var tsPayload = CreateTsPayload();
            var datagram = useRtp ? CreateRtpPacket(tsPayload) : tsPayload;

            using (var sender = new UdpClient(AddressFamily.InterNetwork))
            {
                await sender.SendAsync(datagram, datagram.Length, new IPEndPoint(IPAddress.Loopback, port));
            }

            await Task.Delay(500);
            cancellation.Cancel();

            var result = await recording;

            Assert.Equal(tsPayload.Length, result.BytesWritten);
            Assert.True(File.Exists(outputPath));
            var written = await File.ReadAllBytesAsync(outputPath);
            Assert.Equal(tsPayload.Length, written.Length);
            Assert.Equal(0x47, written[0]);
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task RecordsOutOfOrderRtpPacketsInSequenceOrder()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rtsp-rtp-order-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(directory, "recording.ts");
        var port = GetFreeUdpPort();
        var task = new RecordingTask
        {
            Id = 1,
            ChannelId = 1,
            ChannelName = "Test",
            TaskName = "Live_Order",
            IsLive = true,
            Url = $"rtp://127.0.0.1:{port}",
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow.AddMinutes(1)
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var service = new UdpLiveRecordingService();
        var recording = service.RecordAsync(
            task,
            outputPath,
            targetDuration: null,
            recordingTransport: "MP2T/TCP",
            onProgress: _ => { },
            cancellationToken: cancellation.Token);

        try
        {
            await WaitUntilAsync(() => File.Exists(outputPath), TimeSpan.FromSeconds(3));
            using (var sender = new UdpClient(AddressFamily.InterNetwork))
            {
                foreach (var sequence in new ushort[] { 3, 1, 4, 2 })
                {
                    var payload = new byte[188];
                    payload[0] = 0x47;
                    payload[1] = (byte)sequence;
                    var datagram = CreateRtpPacket(payload, sequence);
                    await sender.SendAsync(datagram, datagram.Length, new IPEndPoint(IPAddress.Loopback, port));
                }
            }

            await Task.Delay(500);
            cancellation.Cancel();
            await recording;

            var written = await File.ReadAllBytesAsync(outputPath);
            Assert.Equal(188 * 4, written.Length);
            Assert.Equal([1, 2, 3, 4], new[] { written[1], written[189], written[377], written[565] });
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static int GetFreeUdpPort()
    {
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    }

    private static byte[] CreateTsPayload()
    {
        const int packetCount = 7;
        var payload = new byte[188 * packetCount];
        for (var index = 0; index < packetCount; index++)
        {
            payload[index * 188] = 0x47;
        }

        return payload;
    }

    private static byte[] CreateRtpPacket(byte[] tsPayload, ushort sequence = 0)
    {
        var packet = new byte[12 + tsPayload.Length];
        packet[0] = 0x80;
        packet[1] = 0x21;
        packet[2] = (byte)(sequence >> 8);
        packet[3] = (byte)sequence;
        tsPayload.CopyTo(packet.AsSpan(12));
        return packet;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            await Task.Delay(50);
        }
    }
}
