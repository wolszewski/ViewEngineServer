namespace LiveViewEngine.TcpClient;

public sealed class LiveViewEngineTcpClientOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6000;
    public int QueueCapacity { get; set; } = 16_384;
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
}
