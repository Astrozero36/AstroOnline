using System.Buffers.Binary;

namespace AstroOnline.Server.Net.Protocol;

public static class PacketBuilder
{
    public static byte[] Build(byte type, ReadOnlySpan<byte> payload)
    {
        var totalLen = PacketHeader.SizeBytes + payload.Length;
        var buf = new byte[totalLen];

        buf[0] = PacketHeader.Magic0;
        buf[1] = PacketHeader.Magic1;
        buf[2] = PacketHeader.CurrentVersion;
        buf[3] = type;

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), (uint)payload.Length);

        if (payload.Length > 0)
            payload.CopyTo(buf.AsSpan(PacketHeader.SizeBytes));

        return buf;
    }
}
