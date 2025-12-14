using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace AstroOnline.Server.Net.Net;

public sealed class UdpNetServer : INetServer
{
    private readonly NetConfig _config;
    private readonly ILogger _log;
    private readonly ConcurrentQueue<InboundDatagram> _inbound = new();

    private UdpClient? _udp;
    private Task? _recvLoop;

    public UdpNetServer(NetConfig config, ILogger? logger = null)
    {
        _config = config;
        _log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public ValueTask StartAsync(CancellationToken token)
    {
        if (_udp != null)
            throw new InvalidOperationException("Server already started.");

        var bindIp = IPAddress.Parse(_config.BindAddress);
        var localEp = new IPEndPoint(bindIp, _config.Port);

        _udp = new UdpClient(localEp)
        {
            DontFragment = true
        };

        _log.LogInformation("UDP listening on {BindAddress}:{Port}", _config.BindAddress, _config.Port);

        _recvLoop = Task.Run(() => ReceiveLoopAsync(_udp, token), CancellationToken.None);
        return ValueTask.CompletedTask;
    }

    public bool TryDequeueInbound(out InboundDatagram datagram)
        => _inbound.TryDequeue(out datagram);

    public async ValueTask SendAsync(IPEndPoint remoteEndPoint, ReadOnlyMemory<byte> datagram, CancellationToken token)
    {
        var udp = _udp ?? throw new InvalidOperationException("Server not started.");
        await udp.SendAsync(datagram, remoteEndPoint, token).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;

            try
            {
                result = await udp.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "UDP receive error");
                await Task.Delay(50, token).ConfigureAwait(false);
                continue;
            }

            var bytes = result.Buffer;

            if (bytes.Length == 0 || bytes.Length > _config.MaxDatagramBytes)
                continue;

            var payload = new byte[bytes.Length];
            Buffer.BlockCopy(bytes, 0, payload, 0, bytes.Length);

            _inbound.Enqueue(new InboundDatagram(result.RemoteEndPoint, payload, DateTimeOffset.UtcNow));
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _udp?.Close();
            _udp?.Dispose();
        }
        catch { }
        finally
        {
            _udp = null;
        }

        if (_recvLoop != null)
        {
            try { await _recvLoop.ConfigureAwait(false); } catch { }
            _recvLoop = null;
        }
    }
}
