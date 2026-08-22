namespace ViewEngineServer.WebApp.WebSocket;

internal sealed class ClientConnectionContext(int connectionId)
{
    public int ConnectionId { get; } = connectionId;
    public HashSet<int> ActiveSubscriptionIds { get; } = new();
    public UniqueIdProvider SubscriptionIdProvider { get; } = new();
    public byte[] Buffer { get; } = new byte[16_384];
}