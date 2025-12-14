using AstroOnline.Server.Core.World;

namespace AstroOnline.Server.Core.Commands;

public sealed class MoveCommand : IWorldCommand
{
    public ulong ClientId { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }

    public MoveCommand(ulong clientId, float x, float y, float z)
    {
        ClientId = clientId;
        X = x;
        Y = y;
        Z = z;
    }

    public void Apply(WorldState state)
    {
        // Authoritative: server is the only source of truth for position.
        state.SetPlayerPosition(ClientId, X, Y, Z);
    }
}
