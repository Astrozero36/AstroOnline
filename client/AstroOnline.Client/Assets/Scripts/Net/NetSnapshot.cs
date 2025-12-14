public readonly struct NetSnapshot
{
    public readonly ulong ServerTick;
    public readonly ulong ClientId;
    public readonly float X;
    public readonly float Y;
    public readonly float Z;

    // Server ack of last processed input seq (only meaningful for local player)
    public readonly uint LastInputSeqAck;

    public NetSnapshot(ulong serverTick, ulong clientId, float x, float y, float z, uint lastInputSeqAck)
    {
        ServerTick = serverTick;
        ClientId = clientId;
        X = x;
        Y = y;
        Z = z;
        LastInputSeqAck = lastInputSeqAck;
    }
}
