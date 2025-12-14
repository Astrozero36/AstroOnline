using System.Buffers.Binary;
using Microsoft.Extensions.Logging;

namespace AstroOnline.Server.Net.Protocol;

/// <summary>
/// Fixed 8-byte header:
/// 0..1  Magic      (0xA0 0x01)
/// 2     Version    (1)
/// 3     Type       (application-defined)
/// 4..7  PayloadLen (uint32, little-endian) - number of bytes AFTER the header
/// </summary>
public readonly record struct PacketHeader(
    byte Version,
    byte Type,
    uint PayloadLength
)
{
    public const int SizeBytes = 8;

    public const byte Magic0 = 0xA0;
    public const byte Magic1 = 0x01;

    public const byte CurrentVersion = 1;

    /// <summary>
    /// Parse header + length without enforcing protocol version.
    /// Use this to detect version mismatches and reply (e.g. during CONNECT).
    /// </summary>
    public static bool TryParseRelaxed(ReadOnlySpan<byte> datagram, out PacketHeader header)
    {
        header = default;

        if (datagram.Length < SizeBytes)
            return false;

        if (datagram[0] != Magic0 || datagram[1] != Magic1)
            return false;

        var version = datagram[2];
        var type = datagram[3];
        var payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(4, 4));

        if (datagram.Length != SizeBytes + payloadLen)
            return false;

        header = new PacketHeader(version, type, payloadLen);
        return true;
    }

    // Existing behavior (silent)
    public static bool TryParse(ReadOnlySpan<byte> datagram, out PacketHeader header)
    {
        header = default;

        if (datagram.Length < SizeBytes)
            return false;

        if (datagram[0] != Magic0 || datagram[1] != Magic1)
            return false;

        var version = datagram[2];
        var type = datagram[3];
        var payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(4, 4));

        if (version != CurrentVersion)
            return false;

        if (datagram.Length != SizeBytes + payloadLen)
            return false;

        header = new PacketHeader(version, type, payloadLen);
        return true;
    }

    // New: same parse, but logs why it failed (use in Host)
    public static bool TryParse(ReadOnlySpan<byte> datagram, ILogger log, out PacketHeader header)
    {
        header = default;

        if (datagram.Length < SizeBytes)
        {
            log.LogWarning("DROP: too-short datagram len={Len}", datagram.Length);
            return false;
        }

        if (datagram[0] != Magic0 || datagram[1] != Magic1)
        {
            log.LogWarning("DROP: bad magic b0=0x{B0:X2} b1=0x{B1:X2}", datagram[0], datagram[1]);
            return false;
        }

        var version = datagram[2];
        var type = datagram[3];
        var payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(4, 4));

        if (version != CurrentVersion)
        {
            log.LogWarning("DROP: version mismatch got={Got} expected={Exp} type={Type} len={Len}",
                version, CurrentVersion, type, datagram.Length);
            return false;
        }

        var expectedLen = SizeBytes + payloadLen;
        if (datagram.Length != expectedLen)
        {
            log.LogWarning("DROP: length mismatch type={Type} gotLen={Got} expectedLen={Exp} payloadLen={PayloadLen}",
                type, datagram.Length, expectedLen, payloadLen);
            return false;
        }

        header = new PacketHeader(version, type, payloadLen);
        return true;
    }
}
