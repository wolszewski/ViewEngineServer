using System.Diagnostics;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Views;

namespace LiveViewEngine.Core.Runtime;

public sealed class CollectionRuntime : IDisposable
{
    private readonly CollectionWorker _worker = new();
    private readonly Dictionary<ViewKey, SharedView> _sharedViews = new();
    private readonly Dictionary<SubscriptionKey, ViewportState> _viewports = new();
    private readonly Lock _subscriptionsByConnectionLock = new();
    private readonly Dictionary<int, HashSet<int>> _subscriptionsByConnection = [];
    private readonly SortIndexRegistry _sortIndexRegistry = new();
    private readonly Dictionary<string, IReadOnlyList<FilterSpec>> _filterPresets = new();
    private readonly IViewEngineMetrics? _metrics;
    private readonly LiveViewEngineOptions _options;
    private int _activeSubscriptionCount;
    private int _activeSharedViewCount;

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

    public int ActiveSubscriptionCount => Volatile.Read(ref _activeSubscriptionCount);
    public int ActiveSharedViewCount => Volatile.Read(ref _activeSharedViewCount);
    public int SortIndexCount => _sortIndexRegistry.Count;
    public int WorkerQueueLength => _worker.QueuedCount;

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

        return new MutationResult(IngestResult.Ok(), groups);
    }

    public IReadOnlyList<ViewDelta> HandleSubscribe(SubscribeCommand command)
    {
        var subscriptionKey = command.EffectiveSubscriptionKey;

        if (_viewports.TryGetValue(subscriptionKey, out var existingViewport))
        {
            DetachSubscription(existingViewport);
            if (_viewports.Remove(subscriptionKey))
            {
                DecrementActiveSubscriptionCount();
            }

            RemoveConnectionSubscription(existingViewport);
        }

        if (command.View.FilterPresetId is not null && !_filterPresets.ContainsKey(command.View.FilterPresetId))
        {
            return [];
        }

        var selectedFieldIndexes = ResolveVisibleFieldIndexes(command.View);
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
        IncrementActiveSubscriptionCount();
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
                if (!_viewports.Remove(subscriptionKey, out var viewportState))
                {
                    continue;
                }

                DecrementActiveSubscriptionCount();
                DetachSubscription(viewportState);
                RemoveConnectionSubscription(viewportState);
            }

            return [];
        }

        if (!_viewports.Remove(command.EffectiveSubscriptionKey, out var viewport))
        {
            return [];
        }

        DecrementActiveSubscriptionCount();
        DetachSubscription(viewport);
        RemoveConnectionSubscription(viewport);
        return [];
    }

    public void Dispose()
    {
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
        var sortIndexKey = CreateSortIndexKey(Collection, viewKey, viewKey.CollectionId);
        var sortIndex = _sortIndexRegistry.GetOrCreate(sortIndexKey, Collection);
        if (!_sharedViews.TryGetValue(viewKey, out var view))
        {
            view = new SharedView(viewKey, Collection, sortIndex, _options);
            _sharedViews[viewKey] = view;
            IncrementActiveSharedViewCount();
        }

        return (view, sortIndexKey, _sortIndexRegistry);
    }

    private bool TryGetSharedView(ViewKey key, out SharedView view)
    {
        if (_sharedViews.TryGetValue(key, out var sharedView))
        {
            view = sharedView;
            return true;
        }

        view = null!;
        return false;
    }

    public IngestResult RegisterFilterPreset(string filterPresetId, IReadOnlyList<FilterSpec> filters)
    {
        if (_filterPresets.ContainsKey(filterPresetId))
        {
            return IngestResult.Fail(
                $"Filter preset '{filterPresetId}' is already registered and cannot be overwritten.");
        }

        _filterPresets[filterPresetId] = filters;
        return IngestResult.Ok();
    }

    private ViewKey ResolveViewKey(ViewDefinition def)
    {
        if (def.FilterPresetId is null)
        {
            return ViewKey.From(def);
        }

        var baseFilters = _filterPresets[def.FilterPresetId];
        if (baseFilters.Count == 0)
        {
            return ViewKey.From(def);
        }

        var combined = def.Filters.Count > 0
            ? [.. baseFilters, .. def.Filters]
            : baseFilters;
        return new ViewKey(def.CollectionId, def.FilterPresetId, def.SortColumn, def.SortAscending, combined);
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

    public bool ContainsSubscription(SubscriptionKey subscriptionKey)
    {
        lock (_subscriptionsByConnectionLock)
        {
            return _subscriptionsByConnection.TryGetValue(subscriptionKey.ConnectionId, out var subscriptions) &&
                   subscriptions.Contains(subscriptionKey.SubscriptionId);
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
            if (_sharedViews.Remove(viewport.ViewKey))
            {
                DecrementActiveSharedViewCount();
                view.Dispose();
            }
        }

        if (sortIndex.SubscriberCount == 0)
        {
            _sortIndexRegistry.FlagForRemoval(new SortIndexKey(viewport.ViewKey.CollectionId, sortIndex.FieldIndex));
        }
    }

    private void IncrementActiveSubscriptionCount()
    {
        Interlocked.Increment(ref _activeSubscriptionCount);
        _metrics?.RecordActiveSubscriptionDelta(1, Collection.Schema.CollectionName);
    }

    private void DecrementActiveSubscriptionCount()
    {
        Interlocked.Decrement(ref _activeSubscriptionCount);
        _metrics?.RecordActiveSubscriptionDelta(-1, Collection.Schema.CollectionName);
    }

    private void IncrementActiveSharedViewCount()
    {
        Interlocked.Increment(ref _activeSharedViewCount);
        _metrics?.RecordActiveSharedViewDelta(1, Collection.Schema.CollectionName);
    }

    private void DecrementActiveSharedViewCount()
    {
        Interlocked.Decrement(ref _activeSharedViewCount);
        _metrics?.RecordActiveSharedViewDelta(-1, Collection.Schema.CollectionName);
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