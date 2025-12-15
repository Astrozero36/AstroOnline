using System.Buffers.Binary;

namespace AstroOnline.Server.Net.Protocol;

public static class PacketBuilder
{
    public static byte[] Build(byte type, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[PacketHeader.SizeBytes + payload.Length];

        buffer[0] = ProtocolConstants.Magic0;
        buffer[1] = ProtocolConstants.Magic1;
        buffer[2] = ProtocolConstants.Version;
        buffer[3] = type;

        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(4, 4),
            (uint)payload.Length
        );

        payload.CopyTo(buffer.AsSpan(PacketHeader.SizeBytes));

        return buffer;
    }
}
