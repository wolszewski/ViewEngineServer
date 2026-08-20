namespace ViewEngineServer.WebApp.Tcp;

public sealed class TcpIngestOptions
{
    public bool Enabled { get; init; } = true;
    public string ListenAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 6000;
    public int Backlog { get; init; } = 128;
    public int MaxFrameLengthBytes { get; init; } = 1_048_576;
    public int CollectionQueueCapacity { get; init; } = 100_000;
    public bool EnableAsyncAcks { get; init; } = true;
}
