public sealed class NetClientConfig
{
    public string ServerIp { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 7777;
    public int PingIntervalMs { get; set; } = 1000;
}
