using AstroOnline.Server.Core.World;

namespace AstroOnline.Server.Core.Commands;

public sealed class InputCommand : IWorldCommand
{
    public ulong ClientId { get; }
    public uint Seq { get; }
    public float InputX { get; }
    public float InputZ { get; }

    public InputCommand(ulong clientId, uint seq, float inputX, float inputZ)
    {
        ClientId = clientId;
        Seq = seq;
        InputX = inputX;
        InputZ = inputZ;
    }

    public void Apply(WorldState state)
    {
        state.SetPlayerInput(ClientId, Seq, InputX, InputZ);
    }
}
