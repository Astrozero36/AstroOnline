using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

public sealed class NetClient : IDisposable
{
    private const byte Magic0 = 0xA0;
    private const byte Magic1 = 0x01;
    private const byte CurrentVersion = 0x01;

    // Packet types
    private const byte TypePing = 0x01;
    private const byte TypePong = 0x02;
    private const byte TypeConnect = 0x0A;
    private const byte TypeAccept = 0x0B;
    private const byte TypeReject = 0x0C;
    private const byte TypeSnapshot = 0x14;
    private const byte TypeInput = 0x1F;

    public bool IsConnected { get; private set; }
    public ulong ClientId { get; private set; }
    public int LastRttMs { get; private set; }

    private readonly NetClientConfig _config;
    private readonly UdpClient _udp;
    private CancellationTokenSource _cts;

    private readonly ConcurrentQueue<NetSnapshot> _snapshots = new();

    private int _forceReconnectRequested;

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
        WriteHeader(pkt, CurrentVersion, TypeInput, 12);

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

        _ = _udp.SendAsync(pkt, pkt.Length, _config.ServerIp, _config.ServerPort);
    }

    private async Task RunHandshakeAndPingLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (IsConnected && Interlocked.Exchange(ref _forceReconnectRequested, 0) != 0)
                {
                    IsConnected = false;
                    ClientId = 0;
                }

                while (!token.IsCancellationRequested && !IsConnected)
                {
                    byte[] connect = new byte[8];
                    WriteHeader(connect, CurrentVersion, TypeConnect, 0);

                    await _udp.SendAsync(connect, connect.Length, _config.ServerIp, _config.ServerPort);

                    int waitedMs = 0;
                    const int stepMs = 25;
                    const int attemptWindowMs = 750;

                    while (!token.IsCancellationRequested && !IsConnected && waitedMs < attemptWindowMs)
                    {
                        if (Interlocked.Exchange(ref _forceReconnectRequested, 0) != 0)
                            break;

                        await Task.Delay(stepMs, token);
                        waitedMs += stepMs;
                    }
                }

                while (!token.IsCancellationRequested && IsConnected)
                {
                    await SendPingAsync();
                    await Task.Delay(_config.PingIntervalMs, token);

                    if (Interlocked.Exchange(ref _forceReconnectRequested, 0) != 0)
                    {
                        IsConnected = false;
                        ClientId = 0;
                        break;
                    }
                }
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
        WriteHeader(ping, CurrentVersion, TypePing, 4);

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

                if (buf[0] != Magic0 || buf[1] != Magic1)
                    continue;

                byte version = buf[2];
                byte type = buf[3];
                uint payloadLen = (uint)(buf[4] | (buf[5] << 8) | (buf[6] << 16) | (buf[7] << 24));

                if (buf.Length != 8 + payloadLen)
                    continue;

                // REJECT (12) payloadLen=3:
                // reason(u8), expectedVersion(u8), gotVersion(u8)
                // Intentionally processed even if header.version != CurrentVersion.
                if (type == TypeReject && payloadLen == 3)
                {
                    var reason = (RejectReason)buf[8];
                    byte expected = buf[9];
                    byte got = buf[10];

                    IsConnected = false;
                    ClientId = 0;
                    Interlocked.Exchange(ref _forceReconnectRequested, 1);

                    Console.WriteLine($"NET: REJECT reason={reason} got={got} expected={expected}");
                    continue;
                }

                // For all other packets, enforce that they match our current protocol.
                if (version != CurrentVersion)
                    continue;

                if (type == TypeAccept && payloadLen == 8)
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

                if (type == TypePong && payloadLen == 4)
                {
                    uint echoMs = (uint)(buf[8] | (buf[9] << 8) | (buf[10] << 16) | (buf[11] << 24));
                    uint nowMs = (uint)Environment.TickCount;
                    LastRttMs = unchecked((int)(nowMs - echoMs));
                    continue;
                }

                if (type == TypeSnapshot && payloadLen == 32)
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

    private static void WriteHeader(byte[] packet, byte version, byte type, uint payloadLen)
    {
        packet[0] = Magic0;
        packet[1] = Magic1;
        packet[2] = version;
        packet[3] = type;

        packet[4] = (byte)(payloadLen & 0xFF);
        packet[5] = (byte)((payloadLen >> 8) & 0xFF);
        packet[6] = (byte)((payloadLen >> 16) & 0xFF);
        packet[7] = (byte)((payloadLen >> 24) & 0xFF);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp.Close(); } catch { }
        try { _udp.Dispose(); } catch { }
    }
}
