namespace ViewEngineServer.WebApp.WebSocket;

public sealed class SubscriptionAcceptedPayload
{
    public required int SubscriptionId { get; init; }
    public required IReadOnlyList<string> Fields { get; init; }
    public required bool SnapshotFollows { get; init; }
    public required int StartIndex { get; init; }
    public required int TotalCount { get; init; }
}
