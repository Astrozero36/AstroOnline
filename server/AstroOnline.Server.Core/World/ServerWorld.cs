namespace AstroOnline.Server.Core.World;

public sealed class ServerWorld
{
    public long Tick { get; private set; }

    public void Update()
    {
        Tick++;
        // TODO: advance simulation deterministically
    }
}
