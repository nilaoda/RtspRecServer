using System.Diagnostics;
using RtspRecServer.Server.Recording;

namespace RtspRecServer.Tests.Recording;

public class RtpReorderBufferTests
{
    [Fact]
    public void Flush_ReordersPacketsAndDropsDuplicates()
    {
        using var buffer = new RtpReorderBuffer(TimeSpan.FromMilliseconds(80));
        var now = Stopwatch.GetTimestamp();
        var payload = new byte[] { 1, 2, 3 };

        Assert.Empty(Push(buffer, 3, payload, now));
        Assert.Empty(Push(buffer, 1, payload, now));
        Assert.Empty(Push(buffer, 4, payload, now));
        Assert.Empty(Push(buffer, 2, payload, now));
        Assert.Empty(Push(buffer, 2, payload, now));

        var ready = buffer.Flush();
        try
        {
            Assert.Equal([1L, 2L, 3L, 4L], ready.Select(packet => packet.Sequence));
        }
        finally
        {
            foreach (var packet in ready)
            {
                packet.Dispose();
            }
        }
    }

    [Fact]
    public void Push_ReleasesPacketsAfterWindowAndCountsMissingSequence()
    {
        using var buffer = new RtpReorderBuffer();
        var now = Stopwatch.GetTimestamp();
        var payload = new byte[] { 1 };

        Push(buffer, 1, payload, now);
        Push(buffer, 3, payload, now);

        var afterWindow = now + Stopwatch.Frequency / 4;
        var ready = Push(buffer, 4, payload, afterWindow);
        try
        {
            Assert.Equal([1L, 3L, 4L], ready.Select(packet => packet.Sequence));
            Assert.Equal(1, buffer.MissingSequences);
            Assert.Empty(Push(buffer, 2, payload, afterWindow));
            Assert.Equal(1, buffer.OldPackets);
        }
        finally
        {
            foreach (var packet in ready)
            {
                packet.Dispose();
            }
        }
    }

    [Fact]
    public void Reset_DiscardsPendingPacketsAndAcceptsNewBaseline()
    {
        using var buffer = new RtpReorderBuffer();
        var now = Stopwatch.GetTimestamp();
        var payload = new byte[] { 1 };

        Push(buffer, 100, payload, now);
        Push(buffer, 101, payload, now);
        buffer.Reset();

        Assert.Empty(Push(buffer, 5, payload, now));
        var ready = buffer.Flush();
        try
        {
            Assert.Equal([5L], ready.Select(packet => packet.Sequence));
        }
        finally
        {
            foreach (var packet in ready)
            {
                packet.Dispose();
            }
        }
    }

    [Fact]
    public void Flush_HandlesSequenceWrap()
    {
        using var buffer = new RtpReorderBuffer();
        var now = Stopwatch.GetTimestamp();
        var payload = new byte[] { 1 };

        Push(buffer, 65535, payload, now);
        Push(buffer, 0, payload, now);
        Push(buffer, 1, payload, now);

        var ready = buffer.Flush();
        try
        {
            Assert.Equal([65535L, 65536L, 65537L], ready.Select(packet => packet.Sequence));
        }
        finally
        {
            foreach (var packet in ready)
            {
                packet.Dispose();
            }
        }
    }

    [Fact]
    public void OrderedPacketsBypassWindowWhileGapStillWaitsForReordering()
    {
        using var buffer = new RtpReorderBuffer();
        var start = Stopwatch.GetTimestamp();
        var afterWindow = start + Stopwatch.Frequency / 10;
        using var datagram = new UdpDatagram([1, 2, 3], start);

        Assert.Empty(buffer.Push(65534, datagram, datagram.Payload));
        Assert.False(buffer.TryReleaseInOrder(65535));
        var first = Assert.Single(buffer.ReleaseDue(afterWindow));
        using (first)
        {
            Assert.Same(datagram, first.Datagram);
            Assert.Equal(65534L, first.Sequence);
        }

        Assert.True(buffer.TryReleaseInOrder(65535));
        Assert.True(buffer.TryReleaseInOrder(0));
        Assert.False(buffer.TryReleaseInOrder(2));

        using var two = new UdpDatagram([2], afterWindow);
        Assert.Empty(buffer.Push(2, two, two.Payload));
        using var one = new UdpDatagram([1], afterWindow + 1);
        var released = buffer.Push(1, one, one.Payload);
        try
        {
            Assert.Equal([65537L, 65538L], released.Select(packet => packet.Sequence));
            Assert.Same(two, released[1].Datagram);
            Assert.Equal(0, buffer.MissingSequences);
        }
        finally
        {
            foreach (var packet in released)
            {
                packet.Dispose();
            }
        }
    }

    private static IReadOnlyList<BufferedRtpPacket> Push(RtpReorderBuffer buffer, ushort sequence, byte[] payload, long timestamp)
    {
        using var datagram = new UdpDatagram(payload, timestamp);
        return buffer.Push(sequence, datagram, datagram.Payload);
    }
}
