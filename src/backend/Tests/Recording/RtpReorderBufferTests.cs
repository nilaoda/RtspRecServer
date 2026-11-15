using RtspRecServer.Server.Recording;

namespace RtspRecServer.Tests.Recording;

public class RtpReorderBufferTests
{
    [Fact]
    public void Flush_ReordersPacketsAndDropsDuplicates()
    {
        using var buffer = new RtpReorderBuffer(TimeSpan.FromMilliseconds(80));
        var now = DateTime.UtcNow;
        var payload = new byte[] { 1, 2, 3 };

        Assert.Empty(buffer.Push(3, payload, now));
        Assert.Empty(buffer.Push(1, payload, now));
        Assert.Empty(buffer.Push(4, payload, now));
        Assert.Empty(buffer.Push(2, payload, now));
        Assert.Empty(buffer.Push(2, payload, now));

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
    public void Flush_KeepsOrderWhenPacketsAreMissing()
    {
        using var buffer = new RtpReorderBuffer();
        var now = DateTime.UtcNow;
        var payload = new byte[] { 1 };

        buffer.Push(1, payload, now);
        buffer.Push(3, payload, now);

        var ready = buffer.Flush();
        try
        {
            Assert.Equal([1L, 3L], ready.Select(packet => packet.Sequence));
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
        var now = DateTime.UtcNow;
        var payload = new byte[] { 1 };

        buffer.Push(65535, payload, now);
        buffer.Push(0, payload, now);
        buffer.Push(1, payload, now);

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
}
