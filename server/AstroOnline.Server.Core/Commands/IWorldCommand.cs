namespace AstroOnline.Server.Core.Commands;

public interface IWorldCommand
{
    void Apply(World.WorldState state);
}
