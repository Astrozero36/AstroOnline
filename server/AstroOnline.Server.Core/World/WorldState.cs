using System.Collections.Generic;

namespace AstroOnline.Server.Core.World;

public sealed class WorldState
{
    public long Tick { get; internal set; }

    private readonly Dictionary<ulong, PlayerState> _players = new();
    public IReadOnlyDictionary<ulong, PlayerState> Players => _players;

    public PlayerState GetOrCreatePlayer(ulong clientId)
    {
        if (!_players.TryGetValue(clientId, out var p))
        {
            p = new PlayerState(clientId);
            _players.Add(clientId, p);
        }

        return p;
    }

    public bool TryGetPlayer(ulong clientId, out PlayerState player)
        => _players.TryGetValue(clientId, out player!);

    public void SetPlayerPosition(ulong clientId, float x, float y, float z)
    {
        var p = GetOrCreatePlayer(clientId);
        p.X = x;
        p.Y = y;
        p.Z = z;
    }

    public void SetPlayerInput(ulong clientId, uint seq, float inputX, float inputZ)
    {
        var p = GetOrCreatePlayer(clientId);

        // Only accept monotonic seq (drop out-of-order / duplicates)
        if (seq <= p.LastInputSeqAck)
            return;

        p.LastInputSeqAck = seq;
        p.InputX = inputX;
        p.InputZ = inputZ;
    }

    public sealed class PlayerState
    {
        public ulong ClientId { get; }

        public float X { get; internal set; }
        public float Y { get; internal set; }
        public float Z { get; internal set; }

        public float InputX { get; internal set; }
        public float InputZ { get; internal set; }

        // Server-side ack of last processed input seq
        public uint LastInputSeqAck { get; internal set; }

        public PlayerState(ulong clientId)
        {
            ClientId = clientId;
        }
    }
}
