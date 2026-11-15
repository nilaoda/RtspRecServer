using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RtspRecServer.Server.Recording;

internal static class PcrParser
{
    public static bool TryUpdatePcrTicks(ReadOnlySpan<byte> payload, ref long? first, ref long? last, out long elapsedTicks)
    {
        elapsedTicks = 0;
        if (payload.Length < 188)
        {
            return false;
        }

        ref byte payloadRef = ref MemoryMarshal.GetReference(payload);
        var offset = 0;

        while (offset + 188 <= payload.Length)
        {
            if (Unsafe.Add(ref payloadRef, offset) != 0x47)
            {
                offset++;
                continue;
            }

            if (TryGetPcrTicks(payload.Slice(offset, 188), out var pcrTicks))
            {
                first ??= pcrTicks;
                last = pcrTicks;
                if (first.HasValue && last.HasValue)
                {
                    elapsedTicks = CalculateElapsedTicks(first.Value, last.Value);
                    return true;
                }
            }

            offset += 188;
        }

        return false;
    }

    public static bool TryGetPcrTicks(ReadOnlySpan<byte> packet, out long ticks)
    {
        ticks = 0;
        if (packet.Length < 188 || packet[0] != 0x47)
        {
            return false;
        }

        var adaptationFieldControl = (packet[3] >> 4) & 0x3;
        if (adaptationFieldControl != 2 && adaptationFieldControl != 3)
        {
            return false;
        }

        var adaptationLength = packet[4];
        if (adaptationLength < 7 || 5 + adaptationLength > 188)
        {
            return false;
        }

        if ((packet[5] & 0x10) == 0)
        {
            return false;
        }

        var baseIndex = 6;
        var pcrBase = ((long)packet[baseIndex] << 25)
                      | ((long)packet[baseIndex + 1] << 17)
                      | ((long)packet[baseIndex + 2] << 9)
                      | ((long)packet[baseIndex + 3] << 1)
                      | ((long)(packet[baseIndex + 4] >> 7));
        var pcrExt = ((packet[baseIndex + 4] & 0x1) << 8) | packet[baseIndex + 5];

        ticks = pcrBase * 300L + pcrExt;
        return true;
    }

    public static long CalculateElapsedTicks(long firstTicks, long lastTicks)
    {
        var wrap = (1L << 33) * 300L;
        return lastTicks >= firstTicks ? lastTicks - firstTicks : lastTicks + wrap - firstTicks;
    }

    public static double CalculateElapsedSeconds(long firstTicks, long lastTicks)
    {
        return CalculateElapsedTicks(firstTicks, lastTicks) / 27_000_000D;
    }
}
