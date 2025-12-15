public readonly struct NetSnapshot
{
    public readonly ulong ServerTick;
    public readonly uint ServerTimeMs; // monotonic ms since server start (0 if not provided)
    public readonly ulong ClientId;
    public readonly float X;
    public readonly float Y;
    public readonly float Z;

    public readonly uint LastInputSeqAck;

    public NetSnapshot(
        ulong serverTick,
        uint serverTimeMs,
        ulong clientId,
        float x,
        float y,
        float z,
        uint lastInputSeqAck)
    {
        ServerTick = serverTick;
        ServerTimeMs = serverTimeMs;
        ClientId = clientId;
        X = x;
        Y = y;
        Z = z;
        LastInputSeqAck = lastInputSeqAck;
    }
}
