﻿using System.Diagnostics;
using System.Buffers.Binary;
using AstroOnline.Server.Core.Commands;
using AstroOnline.Server.Core.World;
using AstroOnline.Server.Net.Net;
using AstroOnline.Server.Net.Protocol;

namespace AstroOnline.Server.Host;

internal static class Program
{
    public static int Main(string[] args)
    {
        Console.Title = "AstroOnline.Server.Host";

        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, __) =>
        {
            if (!cts.IsCancellationRequested)
                cts.Cancel();
        };

        try
        {
            Run(cts.Token).GetAwaiter().GetResult();
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task Run(CancellationToken token)
    {
        const int tickRate = 20;
        const int snapshotEveryTicks = 1;

        var tickInterval = TimeSpan.FromMilliseconds(1000.0 / tickRate);

        Console.WriteLine($"Server starting. TickRate={tickRate}Hz.");

        var world = new ServerWorld();
        var clients = new ClientRegistry();

        var netConfig = new NetConfig
        {
            BindAddress = "0.0.0.0",
            Port = 7777
        };

        await using var netServer = new UdpNetServer(netConfig);
        await netServer.StartAsync(token);

        var sw = Stopwatch.StartNew();
        var nextTick = sw.Elapsed;

        long tick = 0;

        while (!token.IsCancellationRequested)
        {
            var now = sw.Elapsed;

            if (now < nextTick)
            {
                Thread.Sleep(nextTick - now);
                continue;
            }

            if (now - nextTick > TimeSpan.FromSeconds(1))
                nextTick = now;

            tick++;

            while (netServer.TryDequeueInbound(out var datagram))
            {
                var bytes = datagram.Payload;

                // Relaxed parse: lets us explicitly reject CONNECT on version mismatch.
                if (!PacketHeader.TryParseRelaxed(bytes, out var header))
                    continue;

                // Hard protocol version enforcement.
                if (header.Version != PacketHeader.CurrentVersion)
                {
                    // Only respond to CONNECT; everything else is dropped silently.
                    if (header.Type == 10 && header.PayloadLength == 0)
                    {
                        // REJECT (12) payloadLen=3:
                        // reason(u8), expectedVersion(u8), gotVersion(u8)
                        var rejectPayload = new byte[3];
                        rejectPayload[0] = (byte)RejectReason.ProtocolVersionMismatch;
                        rejectPayload[1] = PacketHeader.CurrentVersion;
                        rejectPayload[2] = header.Version;

                        var reject = PacketBuilder.Build(12, rejectPayload);
                        await netServer.SendAsync(datagram.RemoteEndPoint, reject, token);
                    }

                    continue;
                }

                // CONNECT (10)
                if (header.Type == 10)
                {
                    if (header.PayloadLength != 0)
                        continue;

                    var info = clients.GetOrAdd(datagram.RemoteEndPoint);
                    info.LastSeenUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    var idPayload = new byte[8];
                    BinaryPrimitives.WriteUInt64LittleEndian(idPayload, info.ClientId);

                    var accept = PacketBuilder.Build(11, idPayload);
                    await netServer.SendAsync(info.EndPoint, accept, token);
                    continue;
                }

                // PING (1)
                if (header.Type == 1)
                {
                    if (header.PayloadLength != 4)
                        continue;

                    uint clientTimeMs = BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(PacketHeader.SizeBytes, 4)
                    );

                    world.Enqueue(new PingCommand(datagram.RemoteEndPoint, clientTimeMs));

                    var pongPayload = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(pongPayload, clientTimeMs);

                    var pong = PacketBuilder.Build(2, pongPayload);
                    await netServer.SendAsync(datagram.RemoteEndPoint, pong, token);
                    continue;
                }

                // INPUT (31) payloadLen=12: u32 seq, f32 inputX, f32 inputZ
                if (header.Type == 31)
                {
                    if (header.PayloadLength != 12)
                        continue;

                    if (!clients.TryGet(datagram.RemoteEndPoint, out var info))
                        continue;

                    info.LastSeenUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    uint seq = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));

                    int ixi = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4));
                    int izi = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16, 4));

                    float inputX = BitConverter.Int32BitsToSingle(ixi);
                    float inputZ = BitConverter.Int32BitsToSingle(izi);

                    world.Enqueue(new InputCommand(info.ClientId, seq, inputX, inputZ));
                    continue;
                }
            }

            world.Update();

            if (tick % snapshotEveryTicks == 0)
            {
                foreach (var c in clients.SnapshotAll())
                {
                    var player = world.State.GetOrCreatePlayer(c.ClientId);

                    var payload = new byte[32];

                    BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(0, 8), (ulong)world.State.Tick);
                    BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8, 8), c.ClientId);

                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16, 4), BitConverter.SingleToInt32Bits(player.X));
                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(20, 4), BitConverter.SingleToInt32Bits(player.Y));
                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(24, 4), BitConverter.SingleToInt32Bits(player.Z));

                    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(28, 4), player.LastInputSeqAck);

                    var snapshot = PacketBuilder.Build(20, payload);
                    await netServer.SendAsync(c.EndPoint, snapshot, token);
                }
            }

            nextTick += tickInterval;
        }

        Console.WriteLine("Server stopped.");
    }
}
