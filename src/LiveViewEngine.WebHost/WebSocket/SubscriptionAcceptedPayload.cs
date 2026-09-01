namespace ViewEngineServer.WebApp.WebSocket;

public enum SnapshotFollowsKind
{
    None = 0,
    Immediate = 1,
    Pending = 2
}

public sealed class SubscriptionAcceptedPayload
{
    public required int SubscriptionId { get; init; }
    public required IReadOnlyList<string> Fields { get; init; }
    public required SnapshotFollowsKind SnapshotFollows { get; init; }
    public required int StartIndex { get; init; }
    public required int TotalCount { get; init; }
}

