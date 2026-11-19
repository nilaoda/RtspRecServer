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

        using var firstDatagram = await first.ReadAsync(cancellation.Token);
        using var secondDatagram = await second.ReadAsync(cancellation.Token);
        Assert.Same(firstDatagram, secondDatagram);
        Assert.Equal(3, firstDatagram.Length);
        Assert.Equal(3, secondDatagram.Length);
        Assert.Equal(IPAddress.Loopback, firstDatagram.SourceAddress);
        Assert.True(firstDatagram.SourcePort > 0);
    }

    [Fact]
    public async Task UdpReceiveHub_CountsDropsWhenOneSubscriptionIsFull()
    {
        var port = GetFreeUdpPort();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var hub = new UdpReceiveHub();
        await using var slow = hub.Subscribe(null, null, port, capacity: 1);
        await using var fast = hub.Subscribe(null, null, port, capacity: 2);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);

        await sender.SendAsync(new byte[] { 1 }, 1, endpoint);
        using var first = await fast.ReadAsync(cancellation.Token);
        await sender.SendAsync(new byte[] { 2 }, 1, endpoint);
        using var second = await fast.ReadAsync(cancellation.Token);

        Assert.Equal(1, Volatile.Read(ref slow.DroppedCount));
        using var retained = await slow.ReadAsync(cancellation.Token);
        Assert.Equal(1, retained.Payload.Span[0]);
        Assert.Equal(2, second.Payload.Span[0]);
    }

    [Fact]
    public async Task UdpSubscription_BoundsLargeDatagramsByPooledBufferSize()
    {
        var drops = 0;
        await using var subscription = new UdpSubscription(null, 16384, _ => { }, _ => drops++);
        using var datagram = new UdpDatagram(new byte[65507], 0);
        const int packetsAtLimit = 32 * 1024 * 1024 / 65536;
        for (var i = 0; i < packetsAtLimit; i++)
        {
            datagram.Retain();
            Assert.True(subscription.TryWrite(datagram));
        }

        Assert.Equal(0, drops);
        datagram.Retain();
        Assert.True(subscription.TryWrite(datagram));
        Assert.Equal(1, drops);

        using var dequeued = await subscription.ReadAsync(CancellationToken.None);
        datagram.Retain();
        Assert.True(subscription.TryWrite(datagram));
        Assert.Equal(1, drops);
    }

    [Fact]
    public void ShouldDeliver_FiltersByMulticastGroupAndSupportsUnicast()
    {
        var group = IPAddress.Parse("239.1.1.1");

        Assert.True(UdpEndpointReceiver.ShouldDeliver(group, group));
        Assert.False(UdpEndpointReceiver.ShouldDeliver(group, IPAddress.Any));
        Assert.False(UdpEndpointReceiver.ShouldDeliver(group, null));
        Assert.False(UdpEndpointReceiver.ShouldDeliver(group, IPAddress.Parse("239.1.1.2")));
        Assert.True(UdpEndpointReceiver.ShouldDeliver(null, IPAddress.Loopback));
        Assert.False(UdpEndpointReceiver.ShouldDeliver(null, group));
    }

    [Fact]
    public void ReceiverKey_UsesGroupBindingOnLinuxAndLocalAddressForMembership()
    {
        var local = IPAddress.Loopback;
        var group = IPAddress.Parse("239.1.1.1");

        var multicastBindAddress = OperatingSystem.IsLinux() ? group : IPAddress.Any;
        Assert.Equal(new UdpReceiverKey(multicastBindAddress, local, 8000), UdpReceiverKey.ForSubscription(group, local, 8000));
        Assert.Equal(new UdpReceiverKey(local, null, 8000), UdpReceiverKey.ForSubscription(null, local, 8000));
    }

    [Fact]
    public async Task IsolatesTwoMulticastGroupsAndUnicastOnSamePort()
    {
        // Linux 和 macOS 支持回环接口加入组播，Windows 的回环接口没有这个前提。
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var port = GetFreeUdpPort();
        var hub = new UdpReceiveHub();
        var group = IPAddress.Parse("239.255.37.44");
        var otherGroup = IPAddress.Parse("239.255.37.45");
        await using var unicast = hub.Subscribe(null, null, port);
        await using var multicast = hub.Subscribe(group, IPAddress.Loopback, port);
        await using var otherMulticast = hub.Subscribe(otherGroup, IPAddress.Loopback, port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        for (byte marker = 0; marker < 8; marker++)
        {
            using var sender = new UdpClient(AddressFamily.InterNetwork);
            await sender.SendAsync(new byte[] { marker }, new IPEndPoint(IPAddress.Loopback, port), deadline.Token);
            using var datagram = await unicast.ReadAsync(deadline.Token);
            Assert.Equal(marker, datagram.Payload.Span[0]);
        }
        Assert.False(multicast.TryRead(out _));

        using var multicastSender = new UdpClient(AddressFamily.InterNetwork);
        multicastSender.MulticastLoopback = true;
        multicastSender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Loopback.GetAddressBytes());
        await multicastSender.SendAsync(new byte[] { 42 }, new IPEndPoint(group, port), deadline.Token);
        await multicastSender.SendAsync(new byte[] { 43 }, new IPEndPoint(otherGroup, port), deadline.Token);
        using var multicastDatagram = await multicast.ReadAsync(deadline.Token);
        using var otherDatagram = await otherMulticast.ReadAsync(deadline.Token);
        Assert.Equal(42, multicastDatagram.Payload.Span[0]);
        Assert.Equal(43, otherDatagram.Payload.Span[0]);
        Assert.False(unicast.TryRead(out _));
        Assert.False(multicast.TryRead(out _));
        Assert.False(otherMulticast.TryRead(out _));
    }

    [Fact]
    public void LinuxBatchReceiver_PreservesOrderSourceAndLargeDatagrams()
    {
        if (!LinuxUdpBatchReceiver.IsSupported)
        {
            return;
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 1024 * 1024
        };
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)socket.LocalEndPoint!;
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var senderEndpoint = (IPEndPoint)sender.Client.LocalEndPoint!;
        using var batch = new LinuxUdpBatchReceiver(socket.SafeHandle, endpoint.Port);
        var payload = new byte[2048];
        for (var i = 0; i <= LinuxUdpBatchReceiver.BatchSize; i++)
        {
            payload[0] = (byte)i;
            sender.Send(payload, payload.Length, endpoint);
        }

        Assert.Equal(LinuxUdpBatchReceiver.BatchSize, batch.Read());
        for (var i = 0; i < LinuxUdpBatchReceiver.BatchSize; i++)
        {
            Assert.True(batch.TryGetPacket(i, out var data, out var address, out var port));
            Assert.Equal(payload.Length, data.Length);
            Assert.Equal(i, data[0]);
            Assert.Equal(senderEndpoint.Address, address);
            Assert.Equal(senderEndpoint.Port, port);
        }

        Assert.Equal(1, batch.Read());
        Assert.True(batch.TryGetPacket(0, out var last, out _, out _));
        Assert.Equal(LinuxUdpBatchReceiver.BatchSize, last[0]);
        Assert.Equal(0, batch.Read());
    }

    [Fact]
    public async Task DrainsQueuedAndReorderedRtpWhenStoppedDuringWrite()
    {
        var port = GetFreeUdpPort();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var sink = new PausedWriteStream();
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var hub = new UdpReceiveHub();
        var service = new UdpLiveRecordingService(TimeSpan.FromSeconds(30), 5, receiverHub: hub);
        var recording = service.RecordAsync(
            new RecordingTask { Url = $"rtp://127.0.0.1:{port}", IsLive = true },
            string.Empty, null, "MP2T/TCP", _ => { }, stop.Token, sink);
        await using var observer = hub.Subscribe(null, null, port);

        var payload = CreateTsPayload();
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        try
        {
            await sender.SendAsync(CreateRtpPacket(payload, sequence: 1), endpoint, deadline.Token);
            await sender.SendAsync(CreateRtpPacket(payload, sequence: 2), endpoint, deadline.Token);
            await sink.FirstWrite.Task.WaitAsync(deadline.Token);
            await sender.SendAsync(CreateRtpPacket(payload, sequence: 3), endpoint, deadline.Token);
            // 观察者在录制后订阅，读到序号 3 时录制队列已经收到它。
            for (var sequence = 1; sequence <= 3; sequence++)
            {
                using var observed = await observer.ReadAsync(deadline.Token);
                Assert.Equal(sequence, observed.Payload.Span[3]);
            }

            stop.Cancel();
            await sender.SendAsync(CreateRtpPacket(payload, sequence: 4), endpoint, deadline.Token);
            using var next = await observer.ReadAsync(deadline.Token);
            Assert.Equal(4, next.Payload.Span[3]);
        }
        finally
        {
            stop.Cancel();
            sink.Resume.TrySetResult();
        }

        var result = await recording.WaitAsync(deadline.Token);
        Assert.Equal(payload.Length * 3, result.BytesWritten);
        Assert.Equal((byte[])[.. payload, .. payload, .. payload], sink.ToArray());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordsOutOfOrderRtpPacketsInSequenceOrder(bool switchToRawTs)
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
                var endpoint = new IPEndPoint(IPAddress.Loopback, port);
                foreach (var sequence in new ushort[] { 3, 1, 4, 2 })
                {
                    var payload = new byte[188];
                    payload[0] = 0x47;
                    payload[1] = (byte)sequence;
                    var datagram = CreateRtpPacket(payload, sequence);
                    await sender.SendAsync(datagram, datagram.Length, endpoint);
                }

                if (switchToRawTs)
                {
                    var payload = new byte[188];
                    payload[0] = 0x47;
                    payload[1] = 5;
                    await sender.SendAsync(payload, endpoint, cancellation.Token);
                    payload[1] = 6;
                    await sender.SendAsync(CreateRtpPacket(payload, sequence: 1), endpoint, cancellation.Token);
                }
            }

            await Task.Delay(500);
            cancellation.Cancel();
            await recording;

            var written = await File.ReadAllBytesAsync(outputPath);
            byte[] expected = switchToRawTs ? [1, 2, 3, 4, 5, 6] : [1, 2, 3, 4];
            Assert.Equal(188 * expected.Length, written.Length);
            Assert.Equal(expected, Enumerable.Range(0, expected.Length).Select(index => written[index * 188 + 1]));
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
    public async Task RecoversAfterStreamInterruptionAndContinuesSameFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rtsp-udp-recover-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(directory, "recording.ts");
        var port = GetFreeUdpPort();
        var task = new RecordingTask
        {
            Id = 1,
            ChannelId = 1,
            ChannelName = "Test",
            TaskName = "Live_Recover",
            IsLive = true,
            Url = $"rtp://127.0.0.1:{port}",
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow.AddMinutes(1)
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var service = new UdpLiveRecordingService(
            TimeSpan.FromSeconds(1),
            maxIdleRecoveries: 3,
            retryDelayBase: TimeSpan.FromMilliseconds(300));
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
            using var sender = new UdpClient(AddressFamily.InterNetwork);
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);

            var first = new byte[188];
            first[0] = 0x47;
            first[1] = 1;
            var firstDatagram = CreateRtpPacket(first, sequence: 1);
            await sender.SendAsync(firstDatagram, firstDatagram.Length, endpoint);

            // 覆盖无数据判定与重新加入退避，模拟瞬时断流后恢复
            await Task.Delay(1900);

            var second = new byte[188];
            second[0] = 0x47;
            second[1] = 2;
            var secondDatagram = CreateRtpPacket(second, sequence: 2);
            await sender.SendAsync(secondDatagram, secondDatagram.Length, endpoint);

            await Task.Delay(500);
            cancellation.Cancel();
            var result = await recording;

            Assert.Equal(376, result.BytesWritten);
            var written = await File.ReadAllBytesAsync(outputPath);
            Assert.Equal(376, written.Length);
            Assert.Equal(1, written[1]);
            Assert.Equal(2, written[189]);
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
    public async Task FailsAfterExhaustingIdleRecoveries()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rtsp-udp-budget-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(directory, "recording.ts");
        var port = GetFreeUdpPort();
        var task = new RecordingTask
        {
            Id = 1,
            ChannelId = 1,
            ChannelName = "Test",
            TaskName = "Live_Budget",
            IsLive = true,
            Url = $"udp://127.0.0.1:{port}",
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow.AddMinutes(1)
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var service = new UdpLiveRecordingService(
            TimeSpan.FromMilliseconds(200),
            maxIdleRecoveries: 1,
            retryDelayBase: TimeSpan.FromMilliseconds(100));
        var recording = service.RecordAsync(
            task,
            outputPath,
            targetDuration: null,
            recordingTransport: "MP2T/TCP",
            onProgress: _ => { },
            cancellationToken: cancellation.Token);

        try
        {
            var result = await recording;

            Assert.False(result.Success);
            Assert.Equal("直播流无数据超时", result.ErrorMessage);
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
    public async Task ResetsReorderBufferWhenSsrcChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rtsp-udp-ssrc-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(directory, "recording.ts");
        var port = GetFreeUdpPort();
        var task = new RecordingTask
        {
            Id = 1,
            ChannelId = 1,
            ChannelName = "Test",
            TaskName = "Live_Ssrc",
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
            using var sender = new UdpClient(AddressFamily.InterNetwork);
            foreach (var (ssrc, sequence) in new (uint, ushort)[] { (1, 1), (1, 2), (2, 1), (2, 2) })
            {
                var payload = new byte[188];
                payload[0] = 0x47;
                payload[1] = (byte)sequence;
                payload[2] = (byte)ssrc;
                var datagram = CreateRtpPacket(payload, sequence, ssrc);
                await sender.SendAsync(datagram, datagram.Length, new IPEndPoint(IPAddress.Loopback, port));
            }

            await Task.Delay(500);
            cancellation.Cancel();
            await recording;

            var written = await File.ReadAllBytesAsync(outputPath);
            Assert.Equal(188 * 4, written.Length);
            Assert.Equal([1, 2, 1, 2], new[] { written[1], written[189], written[377], written[565] });
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

    private sealed class PausedWriteStream : MemoryStream
    {
        public TaskCompletionSource FirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (FirstWrite.TrySetResult())
            {
                await Resume.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
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

    private static byte[] CreateRtpPacket(byte[] tsPayload, ushort sequence = 0, uint ssrc = 0)
    {
        var packet = new byte[12 + tsPayload.Length];
        packet[0] = 0x80;
        packet[1] = 0x21;
        packet[2] = (byte)(sequence >> 8);
        packet[3] = (byte)sequence;
        packet[8] = (byte)(ssrc >> 24);
        packet[9] = (byte)(ssrc >> 16);
        packet[10] = (byte)(ssrc >> 8);
        packet[11] = (byte)ssrc;
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
