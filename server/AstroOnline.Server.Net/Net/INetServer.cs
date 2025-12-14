using System.Net;

namespace AstroOnline.Server.Net.Net;

public interface INetServer : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken token);
    bool TryDequeueInbound(out InboundDatagram datagram);

    ValueTask SendAsync(IPEndPoint remoteEndPoint, ReadOnlyMemory<byte> datagram, CancellationToken token);
}
