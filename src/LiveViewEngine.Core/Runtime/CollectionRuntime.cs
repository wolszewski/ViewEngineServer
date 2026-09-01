using System.Diagnostics;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.DataIngest;
using LiveViewEngine.Core.Runtime.WorkEvents;
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

        if (command.View.FilterPresetId is not null && !_filterPresets.ContainsKey(command.View.FilterPresetId))
        {
            return [];
        }

        var selectedFieldIndexes = command.View.Fields is { Count: 0 }
            ? [CollectionSchema.PrimaryKeyIndex]
            : ResolveVisibleFieldIndexes(command.View);

        if (_viewports.TryGetValue(subscriptionKey, out var existingViewport))
        {
            DetachSubscription(existingViewport);
            if (_viewports.Remove(subscriptionKey))
            {
                DecrementActiveSubscriptionCount();
            }

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
            View = command.View,
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

        return BuildStreamingSnapshotDeltas(
            subscriptionKey.ToString(),
            view,
            command.StartIndex,
            command.PageSize,
            selectedFieldIndexes);
    }

    public IReadOnlyList<ViewDelta> HandleUpdateView(UpdateViewCommand command)
    {
        if (!_viewports.TryGetValue(command.EffectiveSubscriptionKey, out var viewport))
        {
            throw new InvalidOperationException(
                $"Subscription '{command.EffectiveSubscriptionKey}' was not found.");
        }

        if (!TryBuildUpdatedView(viewport.View, command, out var nextView))
        {
            if (!TryGetSharedView(viewport.ViewKey, out var view))
            {
                return [];
            }

            var newStartIndex = command.StartIndex ?? viewport.StartIndex;
            var newPageSize = command.PageSize ?? viewport.PageSize;

            if (command.SnapshotMode == SnapshotMode.Full)
            {
                viewport.StartIndex = newStartIndex;
                viewport.PageSize = newPageSize;
                return BuildStreamingSnapshotDeltas(
                    viewport.SubscriptionKey.ToString(),
                    view,
                    newStartIndex,
                    newPageSize,
                    viewport.SelectedFieldIndexes);
            }

            return command.SnapshotMode == SnapshotMode.No
                ? UpdateViewportState(
                    viewport,
                    newStartIndex,
                    newPageSize)
                : HandleViewportDelta(
                    viewport,
                    view,
                    newStartIndex,
                    newPageSize);
        }

        return HandleSubscribe(new SubscribeCommand
        {
            ConnectionId = command.ConnectionId,
            SubscriptionId = command.SubscriptionId,
            StartIndex = command.StartIndex ?? viewport.StartIndex,
            PageSize = command.PageSize ?? viewport.PageSize,
            SendSnapshot = command.SnapshotMode != SnapshotMode.No,
            View = nextView
        });
    }

    private IReadOnlyList<ViewDelta> HandleViewportDelta(
        ViewportState viewport,
        SharedView view,
        int startIndex,
        int? pageSize)
    {
        var oldStart = viewport.StartIndex;
        var oldPageSize = viewport.PageSize ?? 0;

        viewport.StartIndex = startIndex;
        if (pageSize.HasValue)
        {
            viewport.PageSize = pageSize;
        }

        var newPageSize = pageSize ?? oldPageSize;
        var newEnd = startIndex + newPageSize;

        return BuildStreamingViewportDeltas(
            viewport,
            view,
            oldStart,
            oldPageSize,
            startIndex,
            newPageSize,
            newEnd);
    }

    private static IReadOnlyList<ViewDelta> UpdateViewportState(
        ViewportState viewport,
        int startIndex,
        int? pageSize)
    {
        viewport.StartIndex = startIndex;
        if (pageSize.HasValue)
        {
            viewport.PageSize = pageSize;
        }

        return [];
    }

    public IReadOnlyList<ViewDelta> HandleChangeViewport(ChangeViewportCommand command)
    {
        return HandleUpdateView(command);
    }

    private IReadOnlyList<ViewDelta> HandleViewportChange(
        ViewportState viewport,
        SharedView view,
        int startIndex,
        int? pageSize)
    {
        var oldStart = viewport.StartIndex;
        var oldPageSize = viewport.PageSize ?? 0;

        viewport.StartIndex = startIndex;
        if (pageSize.HasValue)
        {
            viewport.PageSize = pageSize;
        }

        var newStart = startIndex;
        var newPageSize = pageSize ?? oldPageSize;
        var newEnd = newStart + newPageSize;

        return BuildStreamingViewportDeltas(
            viewport,
            view,
            oldStart,
            oldPageSize,
            newStart,
            newPageSize,
            newEnd);
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

        foreach (var filter in filters)
        {
            if (Collection.Schema.GetFieldIndex(filter.FieldName) < 0)
            {
                return IngestResult.Fail(
                    $"Unknown field '{filter.FieldName}' for collection '{Collection.Schema.CollectionName}'.");
            }
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

    private static bool TryBuildUpdatedView(
        ViewDefinition current,
        UpdateViewCommand command,
        out ViewDefinition nextView)
    {
        bool hasChange = false;
        var normalizedFields = command.Fields is null
            ? current.Fields
            : command.Fields.Count > 0 ? command.Fields : null;
        var normalizedSortColumn = command.SortColumn ?? current.SortColumn;
        var normalizedSortAscending = command.SortAscending ?? current.SortAscending;
        var normalizedFilters = command.Filters ?? current.Filters;

        if (!string.Equals(normalizedSortColumn, current.SortColumn, StringComparison.Ordinal))
        {
            hasChange = true;
        }

        if (normalizedSortAscending != current.SortAscending)
        {
            hasChange = true;
        }

        if (!SequenceEqual(normalizedFilters, current.Filters))
        {
            hasChange = true;
        }

        if (!SequenceEqual(normalizedFields, current.Fields))
        {
            hasChange = true;
        }

        if (!hasChange)
        {
            nextView = current;
            return false;
        }

        nextView = new ViewDefinition
        {
            CollectionId = current.CollectionId,
            FilterPresetId = current.FilterPresetId,
            SortColumn = normalizedSortColumn,
            SortAscending = normalizedSortAscending,
            Filters = normalizedFilters,
            Fields = normalizedFields
        };
        return true;
    }

    private static bool SequenceEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
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
        if (view.Fields is null || view.Fields.Count == 0)
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
        var rowNumbers = new int[_options.SnapshotBatchSize];
        var batchCount = 0;
        var rowNumber = startIndex;
        foreach (int rowIndex in view.EnumeratePageIndexes(startIndex, pageSize))
        {
            rowNumbers[batchCount] = rowNumber++;
            batch[batchCount++] = ProjectRow(Collection.GetRowValues(rowIndex), selectedFieldIndexes);
            if (batchCount == _options.SnapshotBatchSize)
            {
                deltas.Add(CreateSnapshotRowsDelta(viewId, selectedFieldIndexes, batch, rowNumbers, batchCount, isPartial));
                batch = new string?[_options.SnapshotBatchSize][];
                rowNumbers = new int[_options.SnapshotBatchSize];
                batchCount = 0;
            }
        }

        if (batchCount > 0)
        {
            deltas.Add(CreateSnapshotRowsDelta(viewId, selectedFieldIndexes, batch, rowNumbers, batchCount, isPartial));
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
        int[] rowNumbers,
        int batchCount,
        bool isPartial = false)
    {
        return new SnapshotRowsDelta
        {
            ViewId = viewId,
            Schema = Collection.Schema,
            RowNumbers = batchCount == rowNumbers.Length ? rowNumbers : rowNumbers[..batchCount],
            Rows = batchCount == batch.Length ? batch : batch[..batchCount],
            VisibleFieldIndexes = selectedFieldIndexes,
            IsPartial = isPartial
        };
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
}