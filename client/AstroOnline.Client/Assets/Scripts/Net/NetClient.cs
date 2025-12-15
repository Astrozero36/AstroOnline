using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

public sealed class NetClient : IDisposable
{
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

    public ConnectionState State { get; private set; } = ConnectionState.Stopped;

    public RejectReason LastRejectReason { get; private set; } = RejectReason.Unknown;
    public byte LastRejectExpectedVersion { get; private set; }
    public byte LastRejectGotVersion { get; private set; }

    public bool IsTerminal => State == ConnectionState.UpdateRequired;

    public string StatusText
    {
        get
        {
            var state = State;
            var reason = LastRejectReason;
            byte exp = LastRejectExpectedVersion;
            byte got = LastRejectGotVersion;

            if (state == ConnectionState.UpdateRequired ||
                reason == RejectReason.ProtocolVersionMismatch)
                return $"Client out of date (expected={exp}, got={got}). Update required.";

            if (state == ConnectionState.Reconnecting)
            {
                if (reason == RejectReason.ServerFull)
                    return "Server full. Retryingà";
                return "Reconnectingà";
            }

            if (state == ConnectionState.Connecting)
                return "Connectingà";

            if (state == ConnectionState.Connected && IsConnected)
                return "Connected.";

            if (state == ConnectionState.Stopped)
                return "Stopped.";

            return state.ToString();
        }
    }

    public event Action<ConnectionState>? OnStateChanged;
    public event Action<RejectReason, byte, byte>? OnRejected;

    private readonly NetClientConfig _config;
    private readonly UdpClient _udp;
    private CancellationTokenSource? _cts;

    private readonly ConcurrentQueue<NetSnapshot> _snapshots = new();

    private int _forceReconnectRequested;
    private int _terminalStopRequested;

    private int _retryCount;
    private const int MaxRetryDelayMs = 30_000;
    private const int InitialRetryDelayMs = 1_000;

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
        SetState(ConnectionState.Connecting);

        Task.Run(() => ReceiveLoopAsync(_cts.Token));
        Task.Run(() => RunHandshakeAndPingLoopAsync(_cts.Token));
    }

    public bool TryDequeueSnapshot(out NetSnapshot snapshot)
        => _snapshots.TryDequeue(out snapshot);

    public void SendInput(uint seq, float inputX, float inputZ)
    {
        if (!IsConnected)
            return;

        int xi = BitConverter.SingleToInt32Bits(inputX);
        int zi = BitConverter.SingleToInt32Bits(inputZ);

        byte[] pkt = new byte[20];
        WriteHeader(pkt, TypeInput, 12);

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
                if (Volatile.Read(ref _terminalStopRequested) != 0)
                {
                    IsConnected = false;
                    ClientId = 0;
                    SetState(ConnectionState.UpdateRequired);
                    await Task.Delay(250, token);
                    continue;
                }

                if (IsConnected && Interlocked.Exchange(ref _forceReconnectRequested, 0) != 0)
                {
                    IsConnected = false;
                    ClientId = 0;
                    SetState(ConnectionState.Reconnecting);
                }

                while (!token.IsCancellationRequested && !IsConnected)
                {
                    if (Volatile.Read(ref _terminalStopRequested) != 0)
                        break;

                    if (State == ConnectionState.Stopped)
                        SetState(ConnectionState.Connecting);
                    else if (State != ConnectionState.Reconnecting)
                        SetState(ConnectionState.Reconnecting);

                    int delayMs = CalculateBackoffDelayMs();
                    await Task.Delay(delayMs, token);

                    byte[] connect = new byte[8];
                    WriteHeader(connect, TypeConnect, 0);

                    await _udp.SendAsync(connect, connect.Length, _config.ServerIp, _config.ServerPort);

                    int waitedMs = 0;
                    const int stepMs = 25;
                    const int attemptWindowMs = 750;

                    while (!token.IsCancellationRequested && !IsConnected && waitedMs < attemptWindowMs)
                    {
                        if (Volatile.Read(ref _terminalStopRequested) != 0)
                            break;

                        if (Interlocked.Exchange(ref _forceReconnectRequested, 0) != 0)
                            break;

                        await Task.Delay(stepMs, token);
                        waitedMs += stepMs;
                    }

                    _retryCount++;
                }

                if (IsConnected)
                {
                    _retryCount = 0;
                    SetState(ConnectionState.Connected);
                }

                while (!token.IsCancellationRequested && IsConnected)
                {
                    if (Volatile.Read(ref _terminalStopRequested) != 0)
                    {
                        IsConnected = false;
                        ClientId = 0;
                        SetState(ConnectionState.UpdateRequired);
                        break;
                    }

                    await SendPingAsync();
                    await Task.Delay(_config.PingIntervalMs, token);

                    if (Interlocked.Exchange(ref _forceReconnectRequested, 0) != 0)
                    {
                        IsConnected = false;
                        ClientId = 0;
                        SetState(ConnectionState.Reconnecting);
                        break;
                    }
                }
            }
        }
        catch { }
        finally
        {
            IsConnected = false;
            ClientId = 0;
            SetState(ConnectionState.Stopped);
        }
    }

    private int CalculateBackoffDelayMs()
    {
        int exp = Math.Min(_retryCount, 5);
        int delay = InitialRetryDelayMs * (1 << exp);
        return Math.Min(delay, MaxRetryDelayMs);
    }

    private async Task SendPingAsync()
    {
        uint sendMs = (uint)Environment.TickCount;

        byte[] ping = new byte[12];
        WriteHeader(ping, TypePing, 4);

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

                if (buf[0] != ProtocolConstants.Magic0 ||
                    buf[1] != ProtocolConstants.Magic1)
                    continue;

                byte version = buf[2];
                byte type = buf[3];
                uint payloadLen =
                    (uint)(buf[4] |
                          (buf[5] << 8) |
                          (buf[6] << 16) |
                          (buf[7] << 24));

                if (buf.Length != 8 + payloadLen)
                    continue;

                if (type == TypeReject && payloadLen == 3)
                {
                    var reason = (RejectReason)buf[8];
                    byte expected = buf[9];
                    byte got = buf[10];

                    LastRejectReason = reason;
                    LastRejectExpectedVersion = expected;
                    LastRejectGotVersion = got;

                    OnRejected?.Invoke(reason, expected, got);

                    IsConnected = false;
                    ClientId = 0;

                    if (reason == RejectReason.ProtocolVersionMismatch)
                    {
                        Interlocked.Exchange(ref _terminalStopRequested, 1);
                        Interlocked.Exchange(ref _forceReconnectRequested, 0);
                        SetState(ConnectionState.UpdateRequired);
                    }
                    else
                    {
                        Interlocked.Exchange(ref _forceReconnectRequested, 1);
                        SetState(ConnectionState.Reconnecting);
                    }
                    continue;
                }

                if (version != ProtocolConstants.Version)
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
                    _retryCount = 0;
                    SetState(ConnectionState.Connected);
                    continue;
                }

                if (type == TypePong && payloadLen == 4)
                {
                    uint echoMs =
                        (uint)(buf[8] |
                              (buf[9] << 8) |
                              (buf[10] << 16) |
                              (buf[11] << 24));
                    uint nowMs = (uint)Environment.TickCount;
                    LastRttMs = unchecked((int)(nowMs - echoMs));
                    continue;
                }

                // SNAPSHOT:
                // v1: payloadLen=32 (no serverTimeMs)
                // v2: payloadLen=36 (includes serverTimeMs at end)
                if (type == TypeSnapshot && (payloadLen == 32 || payloadLen == 36))
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

                    uint serverTimeMs = 0;
                    if (payloadLen == 36)
                    {
                        serverTimeMs =
                            (uint)(buf[40] |
                                  (buf[41] << 8) |
                                  (buf[42] << 16) |
                                  (buf[43] << 24));
                    }

                    _snapshots.Enqueue(new NetSnapshot(
                        tick,
                        serverTimeMs,
                        cid,
                        BitConverter.Int32BitsToSingle(xi),
                        BitConverter.Int32BitsToSingle(yi),
                        BitConverter.Int32BitsToSingle(zi),
                        ack
                    ));
                }
            }
        }
        catch { }
        finally
        {
            IsConnected = false;
            ClientId = 0;
        }
    }

    private void SetState(ConnectionState state)
    {
        if (State == state)
            return;

        State = state;
        OnStateChanged?.Invoke(state);
    }

    private static void WriteHeader(byte[] packet, byte type, uint payloadLen)
    {
        packet[0] = ProtocolConstants.Magic0;
        packet[1] = ProtocolConstants.Magic1;
        packet[2] = ProtocolConstants.Version;
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
