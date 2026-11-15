namespace RtspRecServer.Tests.TestSupport;

internal static class TsPacketBuilder
{
    public const int PacketSize = 188;

    public static byte[] WithPcr(long pcrTicks)
    {
        var packet = new byte[PacketSize];
        packet[0] = 0x47;
        packet[3] = 0x20;
        packet[4] = 7;
        packet[5] = 0x10;

        var pcrBase = pcrTicks / 300;
        var pcrExt = (int)(pcrTicks % 300);

        packet[6] = (byte)(pcrBase >> 25);
        packet[7] = (byte)(pcrBase >> 17);
        packet[8] = (byte)(pcrBase >> 9);
        packet[9] = (byte)(pcrBase >> 1);
        packet[10] = (byte)((byte)((pcrBase & 1) << 7) | (byte)0x7E | (byte)((pcrExt >> 8) & 1));
        packet[11] = (byte)pcrExt;
        return packet;
    }

    public static byte[] WithoutPcr()
    {
        var packet = new byte[PacketSize];
        packet[0] = 0x47;
        packet[3] = 0x20;
        packet[4] = 1;
        return packet;
    }

    public static byte[] Concatenate(params byte[][] packets)
    {
        var result = new byte[packets.Sum(packet => packet.Length)];
        var offset = 0;
        foreach (var packet in packets)
        {
            packet.CopyTo(result, offset);
            offset += packet.Length;
        }

        return result;
    }
}
