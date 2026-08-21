using System.Collections.Concurrent;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Views;

namespace LiveViewEngine.Core.Runtime;

internal sealed class SegmentRuntime : IDisposable
{
    private readonly CollectionWorker _worker = new();
    private readonly MutationPropagator _propagator = new();
    private long _lastAppliedVersion;

    internal SegmentRuntime(string collectionId, string segmentId, IReadOnlyList<FilterSpec> baseFilters)
    {
        CollectionId = collectionId;
        SegmentId = segmentId;
        BaseFilters = baseFilters;
        SortCollectionId = $"{collectionId}#segment:{segmentId}";
        _worker.Start();
    }

    internal string CollectionId { get; }
    internal string SegmentId { get; }
    internal string SortCollectionId { get; }
    internal IReadOnlyList<FilterSpec> BaseFilters { get; }
    internal SortIndexRegistry SortIndexes { get; } = new();
    internal ConcurrentDictionary<ViewKey, SharedView> SharedViews { get; } = new();
    internal int WorkerQueueLength => _worker.QueuedCount;

    internal Task<T> EnqueueAsync<T>(IWorkItem<T> work, CancellationToken ct = default) =>
        _worker.EnqueueAsync(work, ct);

    internal List<(IReadOnlyList<ViewDelta> Deltas, List<SubscriberTarget> Targets)>? Propagate(
        RowCollection collection,
        ConcurrentDictionary<SubscriptionKey, ViewportState> viewports,
        MutationInfo mutation,
        bool isDelete,
        long version)
    {
        if (version <= _lastAppliedVersion)
        {
            return null;
        }

        if (version != _lastAppliedVersion + 1)
        {
            throw new InvalidOperationException(
                $"Segment '{SegmentId}' expected version {_lastAppliedVersion + 1} but received {version}.");
        }

        _lastAppliedVersion = version;
        if (SortIndexes.Count == 0 || SharedViews.IsEmpty)
        {
            return null;
        }

        return _propagator.Propagate(
            collection,
            SharedViews,
            viewports,
            SortIndexes.GetAllForCollection(SortCollectionId),
            mutation,
            isDelete);
    }

    public void Dispose()
    {
        foreach (var (_, view) in SharedViews)
        {
            view.Dispose();
        }

        _worker.Dispose();
    }
}
