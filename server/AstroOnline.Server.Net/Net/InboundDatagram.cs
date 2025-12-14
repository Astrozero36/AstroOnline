using System.Net;

namespace AstroOnline.Server.Net.Net;

public readonly record struct InboundDatagram(
    IPEndPoint RemoteEndPoint,
    byte[] Payload,
    DateTimeOffset ReceivedAtUtc
);
