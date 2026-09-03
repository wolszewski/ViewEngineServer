namespace LiveViewEngine.Core;

public sealed class LiveViewEngineOptions
{
    public TypedColumnKeepAlive TypedColumnKeepAlive { get; init; } = TypedColumnKeepAlive.WhenReferencedByIndexes;
    public TimeSpan StaleIndexGracePeriod { get; init; } = TimeSpan.FromSeconds(30);
    public int SnapshotBatchSize { get; init; } = 128;

    // When true, all sort indexes and typed columns are created upfront and never reaped.
    public bool EagerIndexing { get; init; } = false;

    // When true, subscribe requests carrying sortColumn/filters are rejected unless the host
    // explicitly opted in via ILiveViewEngineBuilder.AddSorting()/.AddFiltering(). Default false
    // keeps today's fully-permissive behavior (sorting/filtering always available).
    public bool RequireExplicitCapabilities { get; init; } = false;

    // Backing fields are nullable so "never explicitly set" can be distinguished from "explicitly
    // set to true/false" - the effective value then depends on RequireExplicitCapabilities. This
    // makes RequireExplicitCapabilities self-enforcing directly on LiveViewEngineOptions, regardless
    // of construction path (DI via ServiceCollectionExtensions, or a host constructing
    // CollectionStore/CollectionRuntime directly) - a host is never required to separately remember
    // to also set SortingEnabled/FilteringEnabled = false.
    private bool? _sortingEnabled;
    private bool? _filteringEnabled;

    // Not meant to be set directly by hosts under RequireExplicitCapabilities - use
    // ILiveViewEngineBuilder.AddSorting()/.AddFiltering() instead. Defaults to true unless
    // RequireExplicitCapabilities is true and this hasn't been explicitly set.
    public bool SortingEnabled
    {
        get => _sortingEnabled ?? !RequireExplicitCapabilities;
        set => _sortingEnabled = value;
    }

    public bool FilteringEnabled
    {
        get => _filteringEnabled ?? !RequireExplicitCapabilities;
        set => _filteringEnabled = value;
    }
}
