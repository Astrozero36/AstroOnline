using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

public sealed class NetClient : IDisposable
{
    public bool IsConnected { get; private set; }
    public ulong ClientId { get; private set; }
    public int LastRttMs { get; private set; }

    private readonly NetClientConfig _config;
    private readonly UdpClient _udp;
    private CancellationTokenSource _cts;

    private readonly ConcurrentQueue<NetSnapshot> _snapshots = new();

    public NetClient(NetClientConfig config)
    {
        _config = config;
        _udp = new UdpClient(0);
    }

    public void Start()
    {
        if (_cts != null)
            throw new InvalidOperationException("NetClient already started.");

        _cts = new CancellationTokenSource();

        Task.Run(() => ReceiveLoopAsync(_cts.Token));
        Task.Run(() => RunHandshakeAndPingLoopAsync(_cts.Token));
    }

    public bool TryDequeueSnapshot(out NetSnapshot snapshot)
        => _snapshots.TryDequeue(out snapshot);

    // Called from Unity main thread at a fixed rate (20 Hz)
    public void SendInput(uint seq, float inputX, float inputZ)
    {
        if (!IsConnected)
            return;

        int xi = BitConverter.SingleToInt32Bits(inputX);
        int zi = BitConverter.SingleToInt32Bits(inputZ);

        // INPUT (31) payloadLen=12: seq(u32), inputX(f32), inputZ(f32)
        byte[] pkt = new byte[20]; // header(8) + payload(12)
        pkt[0] = 0xA0; pkt[1] = 0x01;
        pkt[2] = 0x01;
        pkt[3] = 31;
        pkt[4] = 12; pkt[5] = 0; pkt[6] = 0; pkt[7] = 0;

        pkt[8] = (byte)(seq & 0xFF);
        pkt[9] = (byte)((seq >> 8) & 0xFF);
        pkt[10] = (byte)((seq >> 16) & 0xFF);
        pkt[11] = (byte)((seq >> 24) & 0xFF);

        pkt[12] = (byte)(xi & 0xFF);
        pkt[13] = (byte)((xi >> 8) & 0xFF);
        pkt[14] = (byte)((xi >> 16) & 0xFF);
        pkt[15] = (byte)((xi >> 24) & 0xFF);

        pkt[16] = (byte)(zi & 0xFF);
        pkt[17] = (byte)((zi >> 8) & 0xFF);
        pkt[18] = (byte)((zi >> 16) & 0xFF);
        pkt[19] = (byte)((zi >> 24) & 0xFF);

        // Fire-and-forget; UDP send is non-blocking enough for this prototype
        _ = _udp.SendAsync(pkt, pkt.Length, _config.ServerIp, _config.ServerPort);
    }

    private async Task RunHandshakeAndPingLoopAsync(CancellationToken token)
    {
        try
        {
            // CONNECT (Type=10)
            byte[] connect =
            {
                0xA0, 0x01,
                0x01,
                0x0A,
                0x00, 0x00, 0x00, 0x00
            };

            await _udp.SendAsync(connect, connect.Length, _config.ServerIp, _config.ServerPort);

            // Wait for ACCEPT
            while (!token.IsCancellationRequested && !IsConnected)
                await Task.Delay(10, token);

            // Ping cadence only (input is sent by Unity main thread)
            while (!token.IsCancellationRequested)
            {
                await SendPingAsync();
                await Task.Delay(_config.PingIntervalMs, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task SendPingAsync()
    {
        uint sendMs = (uint)Environment.TickCount;

        byte[] ping = new byte[12];
        ping[0] = 0xA0; ping[1] = 0x01;
        ping[2] = 0x01;
        ping[3] = 0x01;
        ping[4] = 0x04; ping[5] = 0; ping[6] = 0; ping[7] = 0;

        ping[8] = (byte)(sendMs & 0xFF);
        ping[9] = (byte)((sendMs >> 8) & 0xFF);
        ping[10] = (byte)((sendMs >> 16) & 0xFF);
        ping[11] = (byte)((sendMs >> 24) & 0xFF);

        await _udp.SendAsync(ping, ping.Length, _config.ServerIp, _config.ServerPort);
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var res = await _udp.ReceiveAsync();
                var buf = res.Buffer;

                if (buf.Length < 8)
                    continue;

                if (buf[0] != 0xA0 || buf[1] != 0x01 || buf[2] != 0x01)
                    continue;

                byte type = buf[3];
                uint payloadLen = (uint)(buf[4] | (buf[5] << 8) | (buf[6] << 16) | (buf[7] << 24));

                if (buf.Length != 8 + payloadLen)
                    continue;

                // ACCEPT (11) payloadLen=8
                if (type == 0x0B && payloadLen == 8)
                {
                    ClientId =
                        (ulong)buf[8] |
                        ((ulong)buf[9] << 8) |
                        ((ulong)buf[10] << 16) |
                        ((ulong)buf[11] << 24) |
                        ((ulong)buf[12] << 32) |
                        ((ulong)buf[13] << 40) |
                        ((ulong)buf[14] << 48) |
                        ((ulong)buf[15] << 56);

                    IsConnected = true;
                    continue;
                }

                // PONG (2) payloadLen=4
                if (type == 0x02 && payloadLen == 4)
                {
                    uint echoMs = (uint)(buf[8] | (buf[9] << 8) | (buf[10] << 16) | (buf[11] << 24));
                    uint nowMs = (uint)Environment.TickCount;
                    LastRttMs = unchecked((int)(nowMs - echoMs));
                    continue;
                }

                // SNAPSHOT (20) payloadLen=32
                if (type == 20 && payloadLen == 32)
                {
                    ulong tick =
                        (ulong)buf[8] |
                        ((ulong)buf[9] << 8) |
                        ((ulong)buf[10] << 16) |
                        ((ulong)buf[11] << 24) |
                        ((ulong)buf[12] << 32) |
                        ((ulong)buf[13] << 40) |
                        ((ulong)buf[14] << 48) |
                        ((ulong)buf[15] << 56);

                    ulong cid =
                        (ulong)buf[16] |
                        ((ulong)buf[17] << 8) |
                        ((ulong)buf[18] << 16) |
                        ((ulong)buf[19] << 24) |
                        ((ulong)buf[20] << 32) |
                        ((ulong)buf[21] << 40) |
                        ((ulong)buf[22] << 48) |
                        ((ulong)buf[23] << 56);

                    int xi = buf[24] | (buf[25] << 8) | (buf[26] << 16) | (buf[27] << 24);
                    int yi = buf[28] | (buf[29] << 8) | (buf[30] << 16) | (buf[31] << 24);
                    int zi = buf[32] | (buf[33] << 8) | (buf[34] << 16) | (buf[35] << 24);

                    uint ack =
                        (uint)(buf[36] |
                        (buf[37] << 8) |
                        (buf[38] << 16) |
                        (buf[39] << 24));

                    _snapshots.Enqueue(new NetSnapshot(
                        tick,
                        cid,
                        BitConverter.Int32BitsToSingle(xi),
                        BitConverter.Int32BitsToSingle(yi),
                        BitConverter.Int32BitsToSingle(zi),
                        ack
                    ));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        finally
        {
            IsConnected = false;
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp.Close(); } catch { }
        try { _udp.Dispose(); } catch { }
    }
}
