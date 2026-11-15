using RtspRecServer.Server.Recording;
using RtspRecServer.Tests.TestSupport;

namespace RtspRecServer.Tests.Recording;

public class PcrParserTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(299L)]
    [InlineData(27_000_000L)]
    [InlineData(2_576_980_377_599L)]
    public void TryGetPcrTicks_ReadsEncodedPcr(long ticks)
    {
        var packet = TsPacketBuilder.WithPcr(ticks);

        Assert.True(PcrParser.TryGetPcrTicks(packet, out var actual));
        Assert.Equal(ticks, actual);
    }

    [Fact]
    public void TryGetPcrTicks_ReturnsFalse_WhenPacketIsShort()
    {
        var packet = new byte[TsPacketBuilder.PacketSize - 1];
        packet[0] = 0x47;

        Assert.False(PcrParser.TryGetPcrTicks(packet, out _));
    }

    [Fact]
    public void TryGetPcrTicks_ReturnsFalse_WhenSyncByteIsMissing()
    {
        var packet = TsPacketBuilder.WithPcr(27_000_000);
        packet[0] = 0x00;

        Assert.False(PcrParser.TryGetPcrTicks(packet, out _));
    }

    [Fact]
    public void TryGetPcrTicks_ReturnsFalse_WhenPacketHasNoAdaptationField()
    {
        var packet = TsPacketBuilder.WithPcr(27_000_000);
        packet[3] = 0x10;

        Assert.False(PcrParser.TryGetPcrTicks(packet, out _));
    }

    [Fact]
    public void TryGetPcrTicks_ReturnsFalse_WhenAdaptationFieldIsTooShort()
    {
        var packet = TsPacketBuilder.WithPcr(27_000_000);
        packet[4] = 6;

        Assert.False(PcrParser.TryGetPcrTicks(packet, out _));
    }

    [Fact]
    public void TryGetPcrTicks_ReturnsFalse_WhenAdaptationFieldExceedsPacket()
    {
        var packet = TsPacketBuilder.WithPcr(27_000_000);
        packet[4] = 200;

        Assert.False(PcrParser.TryGetPcrTicks(packet, out _));
    }

    [Fact]
    public void TryGetPcrTicks_ReturnsFalse_WhenPcrFlagIsMissing()
    {
        var packet = TsPacketBuilder.WithPcr(27_000_000);
        packet[5] = 0x00;

        Assert.False(PcrParser.TryGetPcrTicks(packet, out _));
    }

    [Fact]
    public void TryUpdatePcrTicks_TracksElapsedAcrossPayloads()
    {
        long? first = null;
        long? last = null;

        Assert.True(PcrParser.TryUpdatePcrTicks(TsPacketBuilder.WithPcr(0), ref first, ref last, out var firstElapsed));
        Assert.Equal(0, firstElapsed);
        Assert.True(PcrParser.TryUpdatePcrTicks(TsPacketBuilder.WithPcr(27_000_000), ref first, ref last, out var secondElapsed));

        Assert.Equal(0, first);
        Assert.Equal(27_000_000, last);
        Assert.Equal(27_000_000, secondElapsed);
    }

    [Fact]
    public void TryUpdatePcrTicks_ReturnsFalse_WhenPayloadHasNoPcr()
    {
        var payload = TsPacketBuilder.Concatenate(
            TsPacketBuilder.WithoutPcr(),
            TsPacketBuilder.WithoutPcr());
        long? first = null;
        long? last = null;

        var result = PcrParser.TryUpdatePcrTicks(payload, ref first, ref last, out var elapsedTicks);

        Assert.False(result);
        Assert.Null(first);
        Assert.Null(last);
        Assert.Equal(0, elapsedTicks);
    }

    [Fact]
    public void TryUpdatePcrTicks_ScansPastLeadingBytes()
    {
        var payload = new byte[] { 0x00, 0x01 }
            .Concat(TsPacketBuilder.WithPcr(300))
            .ToArray();
        long? first = null;
        long? last = null;

        var result = PcrParser.TryUpdatePcrTicks(payload, ref first, ref last, out _);

        Assert.True(result);
        Assert.Equal(300, first);
        Assert.Equal(300, last);
    }

    [Fact]
    public void TryUpdatePcrTicks_ReturnsFalse_WhenPayloadIsTooShort()
    {
        long? first = null;
        long? last = null;

        Assert.False(PcrParser.TryUpdatePcrTicks(new byte[100], ref first, ref last, out _));
    }

    [Fact]
    public void CalculateElapsedTicks_HandlesPcrWrap()
    {
        var wrap = (1L << 33) * 300L;

        Assert.Equal(1500, PcrParser.CalculateElapsedTicks(wrap - 1000, 500));
    }

    [Fact]
    public void CalculateElapsedTicks_ReturnsZeroForSameValue()
    {
        Assert.Equal(0, PcrParser.CalculateElapsedTicks(12345, 12345));
    }

    [Fact]
    public void CalculateElapsedSeconds_ConvertsTicksToSeconds()
    {
        Assert.Equal(1.0, PcrParser.CalculateElapsedSeconds(0, 27_000_000), 6);
    }
}
