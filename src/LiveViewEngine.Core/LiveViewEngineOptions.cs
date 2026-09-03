namespace LiveViewEngine.Core;

public sealed class LiveViewEngineOptions
{
    public TypedColumnKeepAlive TypedColumnKeepAlive { get; init; } = TypedColumnKeepAlive.WhenReferencedByIndexes;
    public TimeSpan StaleIndexGracePeriod { get; init; } = TimeSpan.FromSeconds(30);
    public int SnapshotBatchSize { get; init; } = 128;

    // When true, all sort indexes and typed columns are created upfront and never reaped.
    public bool EagerIndexing { get; init; } = false;

    // Projects selected fields for outgoing snapshot/delta payloads. Defaults to plain column
    // selection (SelectRowProjector); override for computed/derived columns or field redaction.
    public IRowProjector RowProjector { get; set; } = SelectRowProjector.Instance;

    // When true, subscribe requests carrying sortColumn/filters are rejected unless the host
    // explicitly opted in via ILiveViewEngineBuilder.AddSorting()/.AddFiltering(). Default false
    // keeps today's fully-permissive behavior (sorting/filtering always available).
    public bool RequireExplicitCapabilities { get; init; } = false;

    // Mutated by ILiveViewEngineBuilder.AddSorting()/.AddFiltering() when RequireExplicitCapabilities
    // is set; otherwise always true. Not meant to be set directly by hosts — use the builder.
    public bool SortingEnabled { get; set; } = true;
    public bool FilteringEnabled { get; set; } = true;
}
