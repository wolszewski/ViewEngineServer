namespace LiveViewEngine.Core;

public sealed class LiveViewEngineOptions
{
    public TypedColumnKeepAlive TypedColumnKeepAlive { get; init; } = TypedColumnKeepAlive.WhenReferencedByIndexes;
    public TimeSpan StaleIndexGracePeriod { get; init; } = TimeSpan.FromSeconds(30);
    public int SnapshotBatchSize { get; init; } = 128;

    // When true, all sort indexes and typed columns are created upfront and never reaped.
    public bool EagerIndexing { get; init; } = false;
}
