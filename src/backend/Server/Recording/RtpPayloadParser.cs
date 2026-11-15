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
            if (!TryGetRtpHeaderLength(payload, out var rtpHeaderLength) || rtpHeaderLength >= payload.Length)
            {
                return false;
            }
            dataOffset = rtpHeaderLength;
            dataLength = payload.Length - rtpHeaderLength;
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
