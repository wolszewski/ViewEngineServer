namespace LiveViewEngine.Core;

public sealed class LiveViewEngineOptions
{
    public TypedColumnKeepAlive TypedColumnKeepAlive { get; init; } = TypedColumnKeepAlive.WhenReferencedByIndexes;
    public TimeSpan StaleIndexGracePeriod { get; init; } = TimeSpan.FromSeconds(30);
    public int SnapshotBatchSize { get; init; } = 128;
}
