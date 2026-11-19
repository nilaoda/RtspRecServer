namespace RtspRecServer.Server.Recording;

internal static class RtpPayloadParser
{
    public static bool TryResolvePayloadSegment(
        ReadOnlySpan<byte> payload,
        bool useRtp,
        ref long? firstPcrTicks,
        ref long? lastPcrTicks,
        long targetTicks,
        int packetCount,
        out int dataOffset,
        out int dataLength,
        out bool shouldStop)
    {
        dataOffset = 0;
        dataLength = payload.Length;
        shouldStop = false;

        if (useRtp && payload.Length > 0 && payload[0] != 0x47)
        {
            if (!TryGetRtpPayloadSegment(payload, out dataOffset, out dataLength))
            {
                return false;
            }
        }

        if (dataLength <= 0)
        {
            return false;
        }

        if (targetTicks > 0 && (packetCount % 10 == 0))
        {
            if (PcrParser.TryUpdatePcrTicks(payload.Slice(dataOffset, dataLength), ref firstPcrTicks, ref lastPcrTicks, out var elapsedTicks) &&
                elapsedTicks >= targetTicks)
            {
                shouldStop = true;
            }
        }

        return true;
    }

    public static bool TryGetRtpPayloadSegment(ReadOnlySpan<byte> packet, out int offset, out int length)
    {
        length = 0;
        if (!TryGetRtpHeaderLength(packet, out offset) || packet[0] >> 6 != 2 || offset >= packet.Length)
        {
            return false;
        }

        length = packet.Length - offset;
        if ((packet[0] & 0x20) != 0)
        {
            var padding = packet[^1];
            if (padding == 0 || padding >= length)
            {
                return false;
            }

            length -= padding;
        }

        return true;
    }

    public static bool TryGetRtpHeaderLength(ReadOnlySpan<byte> payload, out int headerLength)
    {
        headerLength = 0;
        if (payload.Length < 12)
        {
            return false;
        }

        var csrcCount = payload[0] & 0x0F;
        headerLength = 12 + csrcCount * 4;
        if (payload.Length < headerLength)
        {
            return false;
        }

        if ((payload[0] & 0x10) != 0)
        {
            if (payload.Length < headerLength + 4)
            {
                return false;
            }

            var extLength = (payload[headerLength + 2] << 8) | payload[headerLength + 3];
            headerLength += 4 + extLength * 4;
            if (payload.Length < headerLength)
            {
                return false;
            }
        }

        return true;
    }
}
