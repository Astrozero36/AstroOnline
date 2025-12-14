using System.Net;
using AstroOnline.Server.Core.World;

namespace AstroOnline.Server.Core.Commands;

public sealed class PingCommand : IWorldCommand
{
    public IPEndPoint RemoteEndPoint { get; }
    public uint ClientTimeMs { get; }

    public PingCommand(IPEndPoint remoteEndPoint, uint clientTimeMs)
    {
        RemoteEndPoint = remoteEndPoint;
        ClientTimeMs = clientTimeMs;
    }

    public void Apply(WorldState state)
    {
        // For now: no world mutation. This exists to prove decode->enqueue->apply.
        // Later we’ll move endpoint-scoped state out of WorldState and into a session layer.
    }
}
