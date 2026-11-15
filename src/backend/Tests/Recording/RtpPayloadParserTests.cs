using RtspRecServer.Server.Recording;
using RtspRecServer.Tests.TestSupport;

namespace RtspRecServer.Tests.Recording;

public class RtpPayloadParserTests
{
    [Fact]
    public void TryGetRtpHeaderLength_ReturnsBaseHeaderLength()
    {
        var payload = new byte[12];
        payload[0] = 0x80;

        Assert.True(RtpPayloadParser.TryGetRtpHeaderLength(payload, out var headerLength));
        Assert.Equal(12, headerLength);
    }

    [Fact]
    public void TryGetRtpHeaderLength_IncludesCsrc()
    {
        var payload = new byte[20];
        payload[0] = 0x82;

        Assert.True(RtpPayloadParser.TryGetRtpHeaderLength(payload, out var headerLength));
        Assert.Equal(20, headerLength);
    }

    [Fact]
    public void TryGetRtpHeaderLength_IncludesExtension()
    {
        var payload = new byte[24];
        payload[0] = 0x90;
        payload[14] = 0;
        payload[15] = 2;

        Assert.True(RtpPayloadParser.TryGetRtpHeaderLength(payload, out var headerLength));
        Assert.Equal(24, headerLength);
    }

    [Fact]
    public void TryGetRtpHeaderLength_ReturnsFalse_WhenPayloadIsShorterThanBaseHeader()
    {
        Assert.False(RtpPayloadParser.TryGetRtpHeaderLength(new byte[11], out _));
    }

    [Fact]
    public void TryGetRtpHeaderLength_ReturnsFalse_WhenCsrcExceedsPayload()
    {
        var payload = new byte[12];
        payload[0] = 0x81;

        Assert.False(RtpPayloadParser.TryGetRtpHeaderLength(payload, out _));
    }

    [Fact]
    public void TryGetRtpHeaderLength_ReturnsFalse_WhenExtensionHeaderIsMissing()
    {
        var payload = new byte[15];
        payload[0] = 0x90;

        Assert.False(RtpPayloadParser.TryGetRtpHeaderLength(payload, out _));
    }

    [Fact]
    public void TryGetRtpHeaderLength_ReturnsFalse_WhenExtensionDataIsTruncated()
    {
        var payload = new byte[23];
        payload[0] = 0x90;
        payload[14] = 0;
        payload[15] = 2;

        Assert.False(RtpPayloadParser.TryGetRtpHeaderLength(payload, out _));
    }

    [Fact]
    public void TryResolvePayloadSegment_ReturnsWholePayload_WhenRtpIsDisabled()
    {
        var payload = new byte[200];
        long? first = null;
        long? last = null;

        var result = RtpPayloadParser.TryResolvePayloadSegment(
            payload, useRtp: false, ref first, ref last, targetTicks: 0, packetCount: 1,
            out var dataOffset, out var dataLength, out var shouldStop);

        Assert.True(result);
        Assert.Equal(0, dataOffset);
        Assert.Equal(200, dataLength);
        Assert.False(shouldStop);
    }

    [Fact]
    public void TryResolvePayloadSegment_ReturnsWholePayload_WhenPayloadStartsWithSyncByte()
    {
        var payload = new byte[200];
        payload[0] = 0x47;
        long? first = null;
        long? last = null;

        var result = RtpPayloadParser.TryResolvePayloadSegment(
            payload, useRtp: true, ref first, ref last, targetTicks: 0, packetCount: 1,
            out var dataOffset, out var dataLength, out _);

        Assert.True(result);
        Assert.Equal(0, dataOffset);
        Assert.Equal(200, dataLength);
    }

    [Fact]
    public void TryResolvePayloadSegment_SkipsRtpHeader()
    {
        var payload = BuildRtpPacket(0x80, 0, 0, TsPacketBuilder.WithoutPcr());
        long? first = null;
        long? last = null;

        var result = RtpPayloadParser.TryResolvePayloadSegment(
            payload, useRtp: true, ref first, ref last, targetTicks: 0, packetCount: 1,
            out var dataOffset, out var dataLength, out _);

        Assert.True(result);
        Assert.Equal(12, dataOffset);
        Assert.Equal(TsPacketBuilder.PacketSize, dataLength);
    }

    [Fact]
    public void TryResolvePayloadSegment_ReturnsFalse_WhenRtpHeaderConsumesPayload()
    {
        var payload = new byte[12];
        payload[0] = 0x80;
        long? first = null;
        long? last = null;

        var result = RtpPayloadParser.TryResolvePayloadSegment(
            payload, useRtp: true, ref first, ref last, targetTicks: 0, packetCount: 1,
            out _, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryResolvePayloadSegment_StopsWhenTargetPcrIsReached()
    {
        var firstPayload = BuildRtpPacket(0x80, 0, 0, TsPacketBuilder.WithPcr(0));
        var secondPayload = BuildRtpPacket(0x80, 0, 0, TsPacketBuilder.WithPcr(27_000_000));
        long? first = null;
        long? last = null;

        var firstResult = RtpPayloadParser.TryResolvePayloadSegment(
            firstPayload, useRtp: true, ref first, ref last, targetTicks: 27_000_000, packetCount: 10,
            out var firstOffset, out var firstLength, out var firstStop);

        var secondResult = RtpPayloadParser.TryResolvePayloadSegment(
            secondPayload, useRtp: true, ref first, ref last, targetTicks: 27_000_000, packetCount: 20,
            out var secondOffset, out var secondLength, out var secondStop);

        Assert.True(firstResult);
        Assert.Equal(12, firstOffset);
        Assert.Equal(TsPacketBuilder.PacketSize, firstLength);
        Assert.False(firstStop);
        Assert.True(secondResult);
        Assert.Equal(12, secondOffset);
        Assert.Equal(TsPacketBuilder.PacketSize, secondLength);
        Assert.True(secondStop);
        Assert.Equal(0, first);
        Assert.Equal(27_000_000, last);
    }

    [Fact]
    public void TryResolvePayloadSegment_DoesNotStopBeforePacketCheckInterval()
    {
        var tsPayload = TsPacketBuilder.Concatenate(
            TsPacketBuilder.WithPcr(0),
            TsPacketBuilder.WithPcr(27_000_000));
        var payload = BuildRtpPacket(0x80, 0, 0, tsPayload);
        long? first = null;
        long? last = null;

        var result = RtpPayloadParser.TryResolvePayloadSegment(
            payload, useRtp: true, ref first, ref last, targetTicks: 27_000_000, packetCount: 9,
            out _, out _, out var shouldStop);

        Assert.True(result);
        Assert.False(shouldStop);
    }

    [Fact]
    public void TryResolvePayloadSegment_DoesNotStopWhenTargetIsNotReached()
    {
        long? first = 0;
        long? last = 0;

        var result = RtpPayloadParser.TryResolvePayloadSegment(
            TsPacketBuilder.WithPcr(27_000_000), useRtp: false, ref first, ref last, targetTicks: 54_000_000, packetCount: 10,
            out _, out _, out var shouldStop);

        Assert.True(result);
        Assert.False(shouldStop);
        Assert.Equal(27_000_000, last);
    }

    [Fact]
    public void TryResolvePayloadSegment_StopsForMp2tTransport()
    {
        long? first = null;
        long? last = null;

        var firstResult = RtpPayloadParser.TryResolvePayloadSegment(
            TsPacketBuilder.WithPcr(0), useRtp: false, ref first, ref last, targetTicks: 27_000_000, packetCount: 10,
            out var firstOffset, out var firstLength, out var firstStop);

        var secondResult = RtpPayloadParser.TryResolvePayloadSegment(
            TsPacketBuilder.WithPcr(27_000_000), useRtp: false, ref first, ref last, targetTicks: 27_000_000, packetCount: 20,
            out var secondOffset, out var secondLength, out var secondStop);

        Assert.True(firstResult);
        Assert.Equal(0, firstOffset);
        Assert.Equal(TsPacketBuilder.PacketSize, firstLength);
        Assert.False(firstStop);
        Assert.True(secondResult);
        Assert.Equal(0, secondOffset);
        Assert.Equal(TsPacketBuilder.PacketSize, secondLength);
        Assert.True(secondStop);
    }

    private static byte[] BuildRtpPacket(byte firstByte, int csrcCount, int extensionWords, byte[] payload)
    {
        var extensionBytes = (firstByte & 0x10) != 0 ? 4 + extensionWords * 4 : 0;
        var headerLength = 12 + csrcCount * 4 + extensionBytes;
        var result = new byte[headerLength + payload.Length];
        result[0] = firstByte;

        if (extensionBytes > 0)
        {
            var extensionOffset = 12 + csrcCount * 4;
            result[extensionOffset + 2] = (byte)(extensionWords >> 8);
            result[extensionOffset + 3] = (byte)extensionWords;
        }

        payload.CopyTo(result, headerLength);
        return result;
    }
}
