namespace ViewEngineServer.WebApp.WebSocket;

public sealed class SubscriptionRejectedPayload
{
    public required int SubscriptionId { get; init; }
    public required string Reason { get; init; }
    public required string Message { get; init; }
}
