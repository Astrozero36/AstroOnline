using System.Collections.Concurrent;
using AstroOnline.Server.Core.Commands;

namespace AstroOnline.Server.Core.World;

public sealed class ServerWorld
{
    private readonly ConcurrentQueue<IWorldCommand> _inbox = new();

    public WorldState State { get; } = new();

    public void Enqueue(IWorldCommand command)
    {
        _inbox.Enqueue(command);
    }

    public void Update()
    {
        State.Tick++;

        // Drain all commands for this tick (authoritative application order = arrival order for now)
        while (_inbox.TryDequeue(out var cmd))
        {
            cmd.Apply(State);
        }

        // TODO: simulation step after commands
    }
}
