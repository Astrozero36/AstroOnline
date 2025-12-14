namespace AstroOnline.Server.Net.Net;

public sealed class NetConfig
{
    // Bind address/port for the authoritative server listener.
    public string BindAddress { get; init; } = "0.0.0.0";
    public int Port { get; init; } = 7777;

    // Safety: maximum UDP payload we’ll accept (bytes).
    public int MaxDatagramBytes { get; init; } = 1200;
}
