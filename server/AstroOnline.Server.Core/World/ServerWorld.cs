using System.Collections.Concurrent;
using AstroOnline.Server.Core.Commands;
using AstroOnline.Server.Net.Protocol;

namespace AstroOnline.Server.Core.World;

public sealed class ServerWorld
{
    private readonly ConcurrentQueue<IWorldCommand> _inbox = new();

    public WorldState State { get; } = new();

    // Authoritative constants (server is source of truth)
    private const float MoveSpeed = ProtocolConstants.MoveSpeed;
    private const float TickDt = ProtocolConstants.TickDt;

    public void Enqueue(IWorldCommand command)
    {
        _inbox.Enqueue(command);
    }

    public void Update()
    {
        State.Tick++;

        // Apply all commands received since last tick
        while (_inbox.TryDequeue(out var cmd))
        {
            cmd.Apply(State);
        }

        // Authoritative simulation step
        foreach (var kv in State.Players)
        {
            var p = kv.Value;

            float ix = p.InputX;
            float iz = p.InputZ;

            // Clamp to unit circle
            float magSq = (ix * ix) + (iz * iz);
            if (magSq > 1f)
            {
                float invMag = 1f / (float)System.Math.Sqrt(magSq);
                ix *= invMag;
                iz *= invMag;
            }

            p.X += ix * MoveSpeed * TickDt;
            p.Z += iz * MoveSpeed * TickDt;
        }
    }
}
