public static class ProtocolConstants
{
    // Magic bytes (packet identification)
    public const byte Magic0 = 0xA0;
    public const byte Magic1 = 0x01;

    // Protocol version
    public const byte Version = 1;

    // Mirrors server-side authoritative constants
    public const int TickRateHz = 20;
    public const float TickDt = 1f / TickRateHz;

    // Movement
    public const float MoveSpeed = 6.0f;
}
