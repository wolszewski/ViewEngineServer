using System.Collections.Concurrent;
using System.Diagnostics;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Views;

namespace LiveViewEngine.Core.Runtime;

public sealed class CollectionRuntime : IDisposable
{
    private readonly CollectionWorker _worker = new();
    private readonly ConcurrentDictionary<ViewKey, SharedView> _sharedViews = new();
    private readonly ConcurrentDictionary<SubscriptionKey, ViewportState> _viewports = new();
    private readonly ConcurrentDictionary<string, SegmentRuntime> _segmentRuntimes = new();
    private readonly Lock _subscriptionsByConnectionLock = new();
    private readonly Dictionary<int, HashSet<int>> _subscriptionsByConnection = [];
    private readonly SortIndexRegistry _sortIndexRegistry = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<FilterSpec>> _segments = new();
    private readonly IViewEngineMetrics? _metrics;
    private readonly LiveViewEngineOptions _options;
    private long _segmentMutationVersion;

    public CollectionRuntime(RowCollection collection, IViewEngineMetrics? metrics, LiveViewEngineOptions? options = null)
    {
        Collection = collection;
        _metrics = metrics;
        _options = options ?? new LiveViewEngineOptions();
        _worker.Start();
        if (_options.EagerIndexing)
        {
            EagerlyInitializeIndexes();
        }
    }

    public RowCollection Collection { get; }
    private readonly MutationPropagator _propagator = new();
    internal ConcurrentDictionary<SubscriptionKey, ViewportState> Viewports => _viewports;

    public int ActiveSubscriptionCount => _viewports.Count;
    public int ActiveSharedViewCount => _sharedViews.Count + _segmentRuntimes.Values.Sum(static x => x.SharedViews.Count);
    public int SortIndexCount => _sortIndexRegistry.Count + _segmentRuntimes.Values.Sum(static x => x.SortIndexes.Count);
    public int WorkerQueueLength => _worker.QueuedCount + _segmentRuntimes.Values.Sum(static x => x.WorkerQueueLength);

    public IEnumerable<(string CollectionId, string FieldName, int RefCount)> GetActiveTypedColumns()
    {
        var collectionId = Collection.Schema.CollectionName;
        foreach (var (fieldName, refCount) in Collection.GetActiveTypedColumns())
        {
            yield return (collectionId, fieldName, refCount);
        }
    }

    public Task<T> EnqueueAsync<T>(IWorkItem<T> work, CancellationToken ct = default) =>
        _worker.EnqueueAsync(work, ct);

    public bool TryGetCollectionIdForConnection(int connectionId, out string? collectionId)
    {
        lock (_subscriptionsByConnectionLock)
        {
            if (_subscriptionsByConnection.ContainsKey(connectionId))
            {
                collectionId = Collection.Schema.CollectionName;
                return true;
            }
        }

        collectionId = null;
        return false;
    }

    public bool TryGetCollectionIdForSubscription(SubscriptionKey subscriptionKey, out string? collectionId)
    {
        collectionId = _viewports.TryGetValue(subscriptionKey, out var viewport) ? viewport.ViewKey.CollectionId : null;
        return collectionId is not null;
    }


    public MutationResult HandleUpsert(UpsertRowCommand command)
    {
        var started = Stopwatch.GetTimestamp();
        var rowAlreadyExisted = Collection.TryGetRowIndex(command.Key, out int existingRowIndex);

        try
        {
            if (rowAlreadyExisted)
            {
                foreach (var sortIndex in _sortIndexRegistry.GetAllForCollection(Collection.Schema.CollectionName))
                {
                    sortIndex.CaptureOldValue(existingRowIndex);
                }

                if (_options.EnableSegmentWorkers)
                {
                    foreach (var segmentRuntime in _segmentRuntimes.Values)
                    {
                        foreach (var sortIndex in segmentRuntime.SortIndexes.GetAllForCollection(segmentRuntime.SortCollectionId))
                        {
                            sortIndex.CaptureOldValue(existingRowIndex);
                        }
                    }
                }
            }

            var mutation = Collection.AddOrUpdate(command.Key, command.Fields);
            List<(IReadOnlyList<ViewDelta>, List<SubscriberTarget>)>? groups = null;
            if (_sortIndexRegistry.Count > 0)
            {
                groups = _propagator.Propagate(
                    Collection,
                    _sharedViews,
                    _viewports,
                    _sortIndexRegistry.GetAllForCollection(Collection.Schema.CollectionName),
                    mutation,
                    isDelete: false);
            }

            if (_options.EnableSegmentWorkers)
            {
                groups = MergeGroups(groups, ExecuteSegmentPropagation(mutation, isDelete: false));
            }

            return new MutationResult(IngestResult.Ok(), groups);
        }
        finally
        {
            var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (rowAlreadyExisted)
            {
                _metrics?.RecordUpdate(durationMs, Collection.Schema.CollectionName);
            }
            else
            {
                _metrics?.RecordInsert(durationMs, Collection.Schema.CollectionName);
            }
        }
    }

    public MutationResult HandleDelete(DeleteRowCommand command)
    {
        var rowAlreadyExisted = Collection.TryGetRowIndex(command.Key, out int existingRowIndex);
        if (rowAlreadyExisted)
        {
            foreach (var sortIndex in _sortIndexRegistry.GetAllForCollection(Collection.Schema.CollectionName))
            {
                sortIndex.CaptureOldValue(existingRowIndex);
            }

            if (_options.EnableSegmentWorkers)
            {
                foreach (var segmentRuntime in _segmentRuntimes.Values)
                {
                    foreach (var sortIndex in segmentRuntime.SortIndexes.GetAllForCollection(segmentRuntime.SortCollectionId))
                    {
                        sortIndex.CaptureOldValue(existingRowIndex);
                    }
                }
            }
        }

        var mutation = Collection.Delete(command.Key);
        if (mutation is null)
        {
            return new MutationResult(IngestResult.Ok(), null);
        }

        List<(IReadOnlyList<ViewDelta>, List<SubscriberTarget>)>? groups = null;
        if (_sortIndexRegistry.Count > 0)
        {
            groups = _propagator.Propagate(
                Collection,
                _sharedViews,
                _viewports,
                _sortIndexRegistry.GetAllForCollection(Collection.Schema.CollectionName),
                mutation,
                isDelete: true);
        }

        if (_options.EnableSegmentWorkers)
        {
            groups = MergeGroups(groups, ExecuteSegmentPropagation(mutation, isDelete: true));
        }

        return new MutationResult(IngestResult.Ok(), groups);
    }

    public IReadOnlyList<ViewDelta> HandleSubscribe(SubscribeCommand command)
    {
        var subscriptionKey = command.EffectiveSubscriptionKey;

        if (command.View.SegmentId is not null && !_segments.ContainsKey(command.View.SegmentId))
        {
            return [];
        }

        var selectedFieldIndexes = ResolveVisibleFieldIndexes(command.View);

        if (_viewports.TryGetValue(subscriptionKey, out var existingViewport))
        {
            DetachSubscription(existingViewport);
            _viewports.TryRemove(subscriptionKey, out _);
            RemoveConnectionSubscription(existingViewport);
        }

        var viewKey = ResolveViewKey(command.View);
        var (view, sortIndexKey, sortIndexRegistry) = GetOrCreateSharedView(viewKey);
        sortIndexRegistry.UnflagForRemoval(sortIndexKey);
        view.AddSubscriber(subscriptionKey);
        view.SortIndex.IncrementSubscribers();

        var viewport = new ViewportState
        {
            SubscriptionKey = subscriptionKey,
            ViewKey = viewKey,
            StartIndex = command.StartIndex,
            PageSize = command.PageSize,
            VisibleColumns = FieldMask.From(selectedFieldIndexes.AsSpan()),
            SelectedFieldIndexes = selectedFieldIndexes
        };
        _viewports[subscriptionKey] = viewport;
        AddConnectionSubscription(viewport);

        if (!command.SendSnapshot)
        {
            return [];
        }

        if (!command.StreamSnapshot)
        {
            var indexes = view.GetPageIndexes(command.StartIndex, command.PageSize);
            return
            [
                new SnapshotDelta
                {
                    ViewId = subscriptionKey.ToString(),
                    Schema = Collection.Schema,
                    TotalCount = view.GetTotalCount(),
                    StartIndex = command.StartIndex,
                    Rows = BuildRows(Collection, indexes, selectedFieldIndexes),
                    VisibleFieldIndexes = selectedFieldIndexes
                }
            ];
        }

        return BuildStreamingSnapshotDeltas(
            subscriptionKey.ToString(),
            view,
            command.StartIndex,
            command.PageSize,
            selectedFieldIndexes);
    }

    public IReadOnlyList<ViewDelta> HandleChangeViewport(ChangeViewportCommand command)
    {
        if (!_viewports.TryGetValue(command.EffectiveSubscriptionKey, out var viewport) ||
            !TryGetSharedView(viewport.ViewKey, out var view))
        {
            return [];
        }

        var oldStart = viewport.StartIndex;
        var oldPageSize = viewport.PageSize ?? 0;
        var oldEnd = oldStart + oldPageSize;

        viewport.StartIndex = command.StartIndex;
        if (command.PageSize.HasValue)
        {
            viewport.PageSize = command.PageSize;
        }

        var newStart = command.StartIndex;
        var newPageSize = command.PageSize ?? oldPageSize;
        var newEnd = newStart + newPageSize;

        if (command.StreamSnapshot)
        {
            return BuildStreamingViewportDeltas(
                viewport,
                view,
                oldStart,
                oldPageSize,
                newStart,
                newPageSize,
                newEnd);
        }

        return BuildIncrementalSnapshot(viewport, view, oldStart, oldEnd, newStart, newPageSize, newEnd);
    }

    private IReadOnlyList<ViewDelta> BuildIncrementalSnapshot(
        ViewportState viewport,
        SharedView view,
        int oldStart, int oldEnd,
        int newStart, int newPageSize, int newEnd)
    {
        var overlapStart = Math.Max(oldStart, newStart);
        var overlapEnd = Math.Min(oldEnd, newEnd);
        var hasOverlap = overlapStart < overlapEnd;

        if (!hasOverlap)
        {
            var indexes = view.GetPageIndexes(newStart, newPageSize);
            return
            [
                new SnapshotDelta
                {
                    ViewId = viewport.SubscriptionKey.ToString(),
                    Schema = Collection.Schema,
                    TotalCount = view.GetTotalCount(),
                    StartIndex = newStart,
                    Rows = BuildRows(Collection, indexes, viewport.SelectedFieldIndexes),
                    VisibleFieldIndexes = viewport.SelectedFieldIndexes
                }
            ];
        }

        var hasBeforeRange = newStart < overlapStart;
        var hasAfterRange = overlapEnd < newEnd;

        if (!hasBeforeRange && !hasAfterRange)
        {
            return [];
        }

        var viewId = viewport.SubscriptionKey.ToString();
        var totalCount = view.GetTotalCount();

        if (hasBeforeRange && !hasAfterRange)
        {
            var indexes = view.GetPageIndexes(newStart, overlapStart - newStart);
            return [new SnapshotDelta
            {
                ViewId = viewId, Schema = Collection.Schema, TotalCount = totalCount,
                StartIndex = newStart, IsPartial = true,
                Rows = BuildRows(Collection, indexes, viewport.SelectedFieldIndexes),
                VisibleFieldIndexes = viewport.SelectedFieldIndexes
            }];
        }

        if (!hasBeforeRange)
        {
            var indexes = view.GetPageIndexes(overlapEnd, newEnd - overlapEnd);
            return [new SnapshotDelta
            {
                ViewId = viewId, Schema = Collection.Schema, TotalCount = totalCount,
                StartIndex = overlapEnd, IsPartial = true,
                Rows = BuildRows(Collection, indexes, viewport.SelectedFieldIndexes),
                VisibleFieldIndexes = viewport.SelectedFieldIndexes
            }];
        }

        var beforeIndexes = view.GetPageIndexes(newStart, overlapStart - newStart);
        var afterIndexes = view.GetPageIndexes(overlapEnd, newEnd - overlapEnd);
        return
        [
            new SnapshotDelta
            {
                ViewId = viewId, Schema = Collection.Schema, TotalCount = totalCount,
                StartIndex = newStart, IsPartial = true,
                Rows = BuildRows(Collection, beforeIndexes, viewport.SelectedFieldIndexes),
                VisibleFieldIndexes = viewport.SelectedFieldIndexes
            },
            new SnapshotDelta
            {
                ViewId = viewId, Schema = Collection.Schema, TotalCount = totalCount,
                StartIndex = overlapEnd, IsPartial = true,
                Rows = BuildRows(Collection, afterIndexes, viewport.SelectedFieldIndexes),
                VisibleFieldIndexes = viewport.SelectedFieldIndexes
            }
        ];
    }

    public IReadOnlyList<ViewDelta> HandleUnsubscribe(UnsubscribeCommand command)
    {
        if (command.SubscriptionId == 0)
        {
            var subscriptionIds = GetConnectionSubscriptionIds(command.ConnectionId);
            if (subscriptionIds.Length == 0)
            {
                return [];
            }

            foreach (var subscriptionId in subscriptionIds)
            {
                var subscriptionKey = new SubscriptionKey(command.ConnectionId, subscriptionId);
                if (!_viewports.TryRemove(subscriptionKey, out var viewportState))
                {
                    continue;
                }

                DetachSubscription(viewportState);
                RemoveConnectionSubscription(viewportState);
            }

            return [];
        }

        if (!_viewports.TryRemove(command.EffectiveSubscriptionKey, out var viewport))
        {
            return [];
        }

        DetachSubscription(viewport);
        RemoveConnectionSubscription(viewport);
        return [];
    }

    public void Dispose()
    {
        foreach (var segmentRuntime in _segmentRuntimes.Values)
        {
            segmentRuntime.Dispose();
        }

        _worker.Dispose();
    }

    internal async Task ReapOnceAsync(CancellationToken ct)
    {
        foreach (var (key, flaggedAt) in _sortIndexRegistry.GetFlagged())
        {
            if (DateTime.UtcNow - flaggedAt >= _options.StaleIndexGracePeriod)
            {
                await EnqueueAsync(new RemoveStaleIndexRuntimeWork(this, key), ct).ConfigureAwait(false);
            }
        }

        foreach (var (fieldIndex, flaggedAt) in Collection.GetPendingTypedColumnDeactivations())
        {
            if (DateTime.UtcNow - flaggedAt >= _options.StaleIndexGracePeriod)
            {
                await EnqueueAsync(new RemoveStaleTypedColumnRuntimeWork(this, fieldIndex), ct).ConfigureAwait(false);
            }
        }

        foreach (var segmentRuntime in _segmentRuntimes.Values)
        {
            foreach (var (key, flaggedAt) in segmentRuntime.SortIndexes.GetFlagged())
            {
                if (DateTime.UtcNow - flaggedAt < _options.StaleIndexGracePeriod)
                {
                    continue;
                }

                if (!segmentRuntime.SortIndexes.TryGet(key, out var index) || index is null)
                {
                    segmentRuntime.SortIndexes.UnflagForRemoval(key);
                    continue;
                }

                if (index.SubscriberCount > 0)
                {
                    segmentRuntime.SortIndexes.UnflagForRemoval(key);
                    continue;
                }

                segmentRuntime.SortIndexes.Remove(key);
                segmentRuntime.SortIndexes.UnflagForRemoval(key);
                Collection.ReleaseTypedFieldRef(key.FieldIndex);
                Collection.TryDeactivatePendingTypedColumn(key.FieldIndex);
            }
        }
    }

    internal bool RemoveStaleTypedColumn(int fieldIndex)
    {
        Collection.TryDeactivatePendingTypedColumn(fieldIndex);
        return true;
    }

    internal bool RemoveStaleIndex(SortIndexKey key)
    {
        if (!_sortIndexRegistry.TryGet(key, out var index) || index is null)
        {
            _sortIndexRegistry.UnflagForRemoval(key);
            return false;
        }

        if (index.SubscriberCount > 0)
        {
            _sortIndexRegistry.UnflagForRemoval(key);
            return false;
        }

        _sortIndexRegistry.Remove(key);
        _sortIndexRegistry.UnflagForRemoval(key);
        Collection.ReleaseTypedFieldRef(key.FieldIndex);
        Collection.TryDeactivatePendingTypedColumn(key.FieldIndex);
        return true;
    }

    private (SharedView View, SortIndexKey SortIndexKey, SortIndexRegistry Registry) GetOrCreateSharedView(ViewKey viewKey)
    {
        if (_options.EnableSegmentWorkers && viewKey.SegmentId is not null)
        {
            var segmentRuntime = GetOrCreateSegmentRuntime(viewKey.SegmentId);
            var sortIndexKey = CreateSortIndexKey(Collection, viewKey, segmentRuntime.SortCollectionId);
            var sortIndex = segmentRuntime.SortIndexes.GetOrCreate(sortIndexKey, Collection);
            var view = segmentRuntime.SharedViews.GetOrAdd(
                viewKey,
                key => new SharedView(key, Collection, sortIndex, _options));
            return (view, sortIndexKey, segmentRuntime.SortIndexes);
        }

        var nonSegmentSortIndexKey = CreateSortIndexKey(Collection, viewKey, viewKey.CollectionId);
        var nonSegmentSortIndex = _sortIndexRegistry.GetOrCreate(nonSegmentSortIndexKey, Collection);
        var nonSegmentView = _sharedViews.GetOrAdd(
            viewKey,
            key => new SharedView(key, Collection, nonSegmentSortIndex, _options));
        return (nonSegmentView, nonSegmentSortIndexKey, _sortIndexRegistry);
    }

    private bool TryGetSharedView(ViewKey key, out SharedView view)
    {
        if (_options.EnableSegmentWorkers && key.SegmentId is not null)
        {
            if (_segmentRuntimes.TryGetValue(key.SegmentId, out var segmentRuntime))
            {
                if (segmentRuntime.SharedViews.TryGetValue(key, out var segmentView))
                {
                    view = segmentView;
                    return true;
                }
            }

            view = null!;
            return false;
        }

        if (_sharedViews.TryGetValue(key, out var nonSegmentView))
        {
            view = nonSegmentView;
            return true;
        }

        view = null!;
        return false;
    }

    private SegmentRuntime GetOrCreateSegmentRuntime(string segmentId)
    {
        var filters = _segments.TryGetValue(segmentId, out var existingFilters) ? existingFilters : [];
        return _segmentRuntimes.GetOrAdd(
            segmentId,
            id => new SegmentRuntime(
                Collection.Schema.CollectionName,
                id,
                filters,
                Volatile.Read(ref _segmentMutationVersion)));
    }

    private List<(IReadOnlyList<ViewDelta>, List<SubscriberTarget>)>? ExecuteSegmentPropagation(
        MutationInfo mutation,
        bool isDelete)
    {
        if (_segmentRuntimes.IsEmpty)
        {
            return null;
        }

        var version = Interlocked.Increment(ref _segmentMutationVersion);
        var tasks = new List<Task<List<(IReadOnlyList<ViewDelta>, List<SubscriberTarget>)>?>>();
        foreach (var segmentRuntime in _segmentRuntimes.Values)
        {
            if (segmentRuntime.SharedViews.IsEmpty)
            {
                continue;
            }

            var work = new SegmentMutationRuntimeWork(segmentRuntime, this, mutation, isDelete, version);
            tasks.Add(segmentRuntime.EnqueueAsync(work));
        }

        if (tasks.Count == 0)
        {
            return null;
        }

        Task.WhenAll(tasks).GetAwaiter().GetResult();

        List<(IReadOnlyList<ViewDelta>, List<SubscriberTarget>)>? merged = null;
        foreach (var task in tasks)
        {
            merged = MergeGroups(merged, task.Result);
        }

        return merged;
    }

    private static List<(IReadOnlyList<ViewDelta>, List<SubscriberTarget>)>? MergeGroups(
        List<(IReadOnlyList<ViewDelta>, List<SubscriberTarget>)>? groups,
        List<(IReadOnlyList<ViewDelta>, List<SubscriberTarget>)>? toAppend)
    {
        if (toAppend is null || toAppend.Count == 0)
        {
            return groups;
        }

        if (groups is null)
        {
            groups = [];
        }

        groups.AddRange(toAppend);
        return groups;
    }

    public IngestResult RegisterSegment(string segmentId, IReadOnlyList<FilterSpec> filters)
    {
        _segments[segmentId] = filters;
        if (_options.EnableSegmentWorkers)
        {
            _segmentRuntimes.GetOrAdd(
                segmentId,
                id => new SegmentRuntime(
                    Collection.Schema.CollectionName,
                    id,
                    filters,
                    Volatile.Read(ref _segmentMutationVersion)));
        }

        return IngestResult.Ok();
    }

    private ViewKey ResolveViewKey(ViewDefinition def)
    {
        if (def.SegmentId is null)
        {
            return ViewKey.From(def);
        }

        var baseFilters = _segments[def.SegmentId];
        if (baseFilters.Count == 0)
        {
            return ViewKey.From(def);
        }

        var combined = def.Filters.Count > 0
            ? [.. baseFilters, .. def.Filters]
            : baseFilters;
        return new ViewKey(def.CollectionId, def.SegmentId, def.SortColumn, def.SortAscending, combined);
    }

    private void EagerlyInitializeIndexes()
    {
        var collectionId = Collection.Schema.CollectionName;
        foreach (var field in Collection.Schema.Fields)
        {
            var key = new SortIndexKey(collectionId, field.FieldIndex);
            _sortIndexRegistry.GetOrCreate(key, Collection);
        }
    }

    private static SortIndexKey CreateSortIndexKey(RowCollection collection, ViewKey key, string sortCollectionId)
    {
        int sortFieldIndex = key.SortColumn is not null
            ? collection.Schema.GetFieldIndex(key.SortColumn)
            : collection.Schema.PrimaryKey.FieldIndex;
        if (sortFieldIndex < 0)
        {
            sortFieldIndex = collection.Schema.PrimaryKey.FieldIndex;
        }

        return new SortIndexKey(sortCollectionId, sortFieldIndex);
    }

    private void AddConnectionSubscription(ViewportState viewport)
    {
        lock (_subscriptionsByConnectionLock)
        {
            if (!_subscriptionsByConnection.TryGetValue(viewport.SubscriptionKey.ConnectionId, out var subscriptions))
            {
                subscriptions = [];
                _subscriptionsByConnection[viewport.SubscriptionKey.ConnectionId] = subscriptions;
            }

            subscriptions.Add(viewport.SubscriptionKey.SubscriptionId);
        }
    }

    private void RemoveConnectionSubscription(ViewportState viewport)
    {
        lock (_subscriptionsByConnectionLock)
        {
            if (!_subscriptionsByConnection.TryGetValue(viewport.SubscriptionKey.ConnectionId, out var subscriptions))
            {
                return;
            }

            subscriptions.Remove(viewport.SubscriptionKey.SubscriptionId);
            if (subscriptions.Count == 0)
            {
                _subscriptionsByConnection.Remove(viewport.SubscriptionKey.ConnectionId);
            }
        }
    }

    private int[] GetConnectionSubscriptionIds(int connectionId)
    {
        lock (_subscriptionsByConnectionLock)
        {
            if (!_subscriptionsByConnection.TryGetValue(connectionId, out var subscriptions))
            {
                return [];
            }

            var result = new int[subscriptions.Count];
            subscriptions.CopyTo(result);
            return result;
        }
    }

    private void DetachSubscription(ViewportState viewport)
    {
        if (!TryGetSharedView(viewport.ViewKey, out var view))
        {
            return;
        }

        view.RemoveSubscriber(viewport.SubscriptionKey);
        var sortIndex = view.SortIndex;
        sortIndex.DecrementSubscribers();

        if (view.IsEmpty)
        {
            if (viewport.ViewKey.SegmentId is not null && _options.EnableSegmentWorkers)
            {
                if (_segmentRuntimes.TryGetValue(viewport.ViewKey.SegmentId, out var segmentRuntime))
                {
                    if (segmentRuntime.SharedViews.TryRemove(viewport.ViewKey, out _))
                    {
                        view.Dispose();
                    }
                }
            }
            else if (_sharedViews.TryRemove(viewport.ViewKey, out _))
            {
                view.Dispose();
            }
        }

        if (sortIndex.SubscriberCount == 0)
        {
            if (viewport.ViewKey.SegmentId is not null &&
                _options.EnableSegmentWorkers &&
                _segmentRuntimes.TryGetValue(viewport.ViewKey.SegmentId, out var segmentRuntime))
            {
                segmentRuntime.SortIndexes.FlagForRemoval(
                    new SortIndexKey(segmentRuntime.SortCollectionId, sortIndex.FieldIndex));
                return;
            }

            _sortIndexRegistry.FlagForRemoval(new SortIndexKey(viewport.ViewKey.CollectionId, sortIndex.FieldIndex));
        }
    }

    private int[] ResolveVisibleFieldIndexes(ViewDefinition view)
    {
        if (view.Fields is null)
        {
            var all = new int[Collection.Schema.Fields.Count];
            for (var i = 0; i < all.Length; i++) { all[i] = i; }
            return all;
        }

        var requestedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fieldName in view.Fields)
        {
            int fieldIndex = Collection.Schema.GetFieldIndex(fieldName);
            if (fieldIndex < 0)
            {
                throw new ArgumentException(
                    $"Unknown field '{fieldName}' for collection '{Collection.Schema.CollectionName}'.",
                    nameof(view.Fields));
            }

            requestedFields.Add(Collection.Schema.Fields[fieldIndex].Name);
        }

        requestedFields.Add(Collection.Schema.Fields[CollectionSchema.PrimaryKeyIndex].Name);
        var indexes = new int[requestedFields.Count];
        var indexCount = 0;
        for (int i = 0; i < Collection.Schema.Fields.Count; i++)
        {
            if (requestedFields.Contains(Collection.Schema.Fields[i].Name))
            {
                indexes[indexCount++] = i;
            }
        }

        return indexes;
    }

    private IReadOnlyList<ViewDelta> BuildStreamingViewportDeltas(
        ViewportState viewport,
        SharedView view,
        int oldStart,
        int oldPageSize,
        int newStart,
        int newPageSize,
        int newEnd)
    {
        var overlapStart = Math.Max(oldStart, newStart);
        var overlapEnd = Math.Min(oldStart + oldPageSize, newEnd);
        var hasOverlap = overlapStart < overlapEnd;

        if (!hasOverlap)
        {
            return BuildStreamingSnapshotDeltas(
                viewport.SubscriptionKey.ToString(),
                view,
                newStart,
                newPageSize,
                viewport.SelectedFieldIndexes,
                isPartial: false);
        }

        var hasBeforeRange = newStart < overlapStart;
        var hasAfterRange = overlapEnd < newEnd;

        if (!hasBeforeRange && !hasAfterRange)
        {
            return [];
        }

        var viewId = viewport.SubscriptionKey.ToString();

        if (hasBeforeRange && !hasAfterRange)
        {
            return BuildStreamingSnapshotDeltas(viewId, view, newStart, overlapStart - newStart,
                viewport.SelectedFieldIndexes, isPartial: true);
        }

        if (!hasBeforeRange)
        {
            return BuildStreamingSnapshotDeltas(viewId, view, overlapEnd, newEnd - overlapEnd,
                viewport.SelectedFieldIndexes, isPartial: true);
        }

        var deltas = new List<ViewDelta>();
        deltas.AddRange(BuildStreamingSnapshotDeltas(viewId, view, newStart, overlapStart - newStart,
            viewport.SelectedFieldIndexes, isPartial: true));
        deltas.AddRange(BuildStreamingSnapshotDeltas(viewId, view, overlapEnd, newEnd - overlapEnd,
            viewport.SelectedFieldIndexes, isPartial: true));
        return deltas;
    }

    private IReadOnlyList<ViewDelta> BuildStreamingSnapshotDeltas(
        string viewId,
        SharedView view,
        int startIndex,
        int? pageSize,
        int[] selectedFieldIndexes,
        bool isPartial = false)
    {
        var deltas = new List<ViewDelta>
        {
            new SnapshotStartDelta
            {
                ViewId = viewId,
                Schema = Collection.Schema,
                TotalCount = view.GetTotalCount(),
                StartIndex = startIndex,
                IsPartial = isPartial,
                VisibleFieldIndexes = selectedFieldIndexes
            }
        };

        var batch = new string?[_options.SnapshotBatchSize][];
        var batchCount = 0;
        foreach (int rowIndex in view.EnumeratePageIndexes(startIndex, pageSize))
        {
            batch[batchCount++] = ProjectRow(Collection.GetRowValues(rowIndex), selectedFieldIndexes);
            if (batchCount == _options.SnapshotBatchSize)
            {
                deltas.Add(CreateSnapshotRowsDelta(viewId, selectedFieldIndexes, batch, batchCount, isPartial));
                batch = new string?[_options.SnapshotBatchSize][];
                batchCount = 0;
            }
        }

        if (batchCount > 0)
        {
            deltas.Add(CreateSnapshotRowsDelta(viewId, selectedFieldIndexes, batch, batchCount, isPartial));
        }

        deltas.Add(new EndOfSnapshotDelta
        {
            ViewId = viewId,
            VisibleFieldIndexes = selectedFieldIndexes
        });
        return deltas;
    }

    private SnapshotRowsDelta CreateSnapshotRowsDelta(
        string viewId,
        int[] selectedFieldIndexes,
        string?[][] batch,
        int batchCount,
        bool isPartial = false)
    {
        return new SnapshotRowsDelta
        {
            ViewId = viewId,
            Schema = Collection.Schema,
            Rows = batchCount == batch.Length ? batch : batch[..batchCount],
            VisibleFieldIndexes = selectedFieldIndexes,
            IsPartial = isPartial
        };
    }

    private static IReadOnlyList<string?[]> BuildRows(RowCollection collection, int[] indexes,
        int[] selectedFieldIndexes)
    {
        var rows = new string?[indexes.Length][];
        for (int i = 0; i < indexes.Length; i++)
        {
            rows[i] = ProjectRow(collection.GetRowValues(indexes[i]), selectedFieldIndexes);
        }

        return rows;
    }

    private static string?[] ProjectRow(string?[] source, int[] selectedFieldIndexes)
    {
        var copy = new string?[selectedFieldIndexes.Length];
        for (int i = 0; i < selectedFieldIndexes.Length; i++)
        {
            copy[i] = source[selectedFieldIndexes[i]];
        }

        return copy;
    }

    private static string?[] CopyRow(string?[] source)
    {
        var copy = new string?[source.Length];
        Array.Copy(source, copy, source.Length);
        return copy;
    }
}