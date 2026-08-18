using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Views;

namespace LiveViewEngine.Core.Runtime;

public sealed class CollectionRuntime : IDisposable
{
    private readonly CollectionWorker _worker = new();
    private readonly ConcurrentDictionary<ViewKey, SharedView> _sharedViews = new();
    private readonly ConcurrentDictionary<SubscriptionKey, ViewportState> _viewports = new();
    private readonly Lock _subscriptionsByConnectionLock = new();
    private readonly Dictionary<int, HashSet<int>> _subscriptionsByConnection = [];
    private readonly SortIndexRegistry _sortIndexRegistry = new();
    private readonly IViewEngineMetrics? _metrics;
    private readonly LiveViewEngineOptions _options;

    public CollectionRuntime(RowCollection collection, IViewEngineMetrics? metrics, LiveViewEngineOptions? options = null)
    {
        Collection = collection;
        _metrics = metrics;
        _options = options ?? new LiveViewEngineOptions();
        _worker.Start();
    }

    public RowCollection Collection { get; }
    private readonly MutationPropagator _propagator = new();

    public int ActiveSubscriptionCount => _viewports.Count;
    public int ActiveSharedViewCount => _sharedViews.Count;
    public int SortIndexCount => _sortIndexRegistry.Count;

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
            }

            var mutation = Collection.AddOrUpdate(command.Key, command.Fields);
            List<(IReadOnlyList<ViewDelta>, List<SubscriberTarget>)>? groups = null;
            if (_sharedViews.Count > 0)
            {
                groups = _propagator.Propagate(Collection, _sharedViews, _viewports, mutation, isDelete: false);
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
        if (_sharedViews.Count > 0)
        {
            groups = _propagator.Propagate(Collection, _sharedViews, _viewports, mutation, isDelete: true);
        }

        return new MutationResult(IngestResult.Ok(), groups);
    }

    public IReadOnlyList<ViewDelta> HandleSubscribe(SubscribeCommand command)
    {
        var subscriptionKey = command.EffectiveSubscriptionKey;
        var selectedFieldIndexes = ResolveVisibleFieldIndexes(command.View);

        if (_viewports.TryGetValue(subscriptionKey, out var existingViewport))
        {
            DetachSubscription(existingViewport);
            _viewports.TryRemove(subscriptionKey, out _);
            RemoveConnectionSubscription(existingViewport);
        }

        var viewKey = ViewKey.From(command.View);
        var sortIndexKey = CreateSortIndexKey(Collection, viewKey);
        var sortIndex = _sortIndexRegistry.GetOrCreate(sortIndexKey, Collection);
        _sortIndexRegistry.UnflagForRemoval(sortIndexKey);
        var view = _sharedViews.GetOrAdd(viewKey, key => new SharedView(key, Collection, sortIndex, _options));
        view.AddSubscriber(subscriptionKey);
        sortIndex.IncrementSubscribers();

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
            !_sharedViews.TryGetValue(viewport.ViewKey, out var view))
        {
            return [];
        }

        viewport.StartIndex = command.StartIndex;
        viewport.PageSize = command.PageSize;

        if (!command.StreamSnapshot)
        {
            var indexes = view.GetPageIndexes(command.StartIndex, command.PageSize);
            return
            [
                new SnapshotDelta
                {
                    ViewId = viewport.SubscriptionKey.ToString(),
                    Schema = Collection.Schema,
                    TotalCount = view.GetTotalCount(),
                    StartIndex = command.StartIndex,
                    Rows = BuildRows(Collection, indexes, viewport.SelectedFieldIndexes),
                    VisibleFieldIndexes = viewport.SelectedFieldIndexes
                }
            ];
        }

        return BuildStreamingSnapshotDeltas(
            viewport.SubscriptionKey.ToString(),
            view,
            command.StartIndex,
            command.PageSize,
            viewport.SelectedFieldIndexes);
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

    public void Dispose() => _worker.Dispose();

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

    private static SortIndexKey CreateSortIndexKey(RowCollection collection, ViewKey key)
    {
        int sortFieldIndex = key.SortColumn is not null
            ? collection.Schema.GetFieldIndex(key.SortColumn)
            : collection.Schema.PrimaryKey.FieldIndex;
        if (sortFieldIndex < 0)
        {
            sortFieldIndex = collection.Schema.PrimaryKey.FieldIndex;
        }

        return new SortIndexKey(key.CollectionId, sortFieldIndex);
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
            return _subscriptionsByConnection.TryGetValue(connectionId, out var subscriptions)
                ? subscriptions.ToArray()
                : [];
        }
    }

    private void DetachSubscription(ViewportState viewport)
    {
        if (!_sharedViews.TryGetValue(viewport.ViewKey, out var view))
        {
            return;
        }

        view.RemoveSubscriber(viewport.SubscriptionKey);
        var sortIndex = view.SortIndex;
        sortIndex.DecrementSubscribers();

        if (view.IsEmpty)
        {
            if (_sharedViews.TryRemove(viewport.ViewKey, out _))
            {
                view.Dispose();
            }
        }

        if (sortIndex.SubscriberCount == 0)
        {
            _sortIndexRegistry.FlagForRemoval(new SortIndexKey(viewport.ViewKey.CollectionId, sortIndex.FieldIndex));
        }
    }

    private int[] ResolveVisibleFieldIndexes(ViewDefinition view)
    {
        if (view.Fields is null)
        {
            return Enumerable.Range(0, Collection.Schema.Fields.Count).ToArray();
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
        var indexes = new List<int>(requestedFields.Count);
        for (int i = 0; i < Collection.Schema.Fields.Count; i++)
        {
            if (requestedFields.Contains(Collection.Schema.Fields[i].Name))
            {
                indexes.Add(i);
            }
        }

        return indexes.ToArray();
    }

    private IReadOnlyList<ViewDelta> BuildStreamingSnapshotDeltas(
        string viewId,
        SharedView view,
        int startIndex,
        int? pageSize,
        int[] selectedFieldIndexes)
    {
        var deltas = new List<ViewDelta>
        {
            new SnapshotStartDelta
            {
                ViewId = viewId,
                Schema = Collection.Schema,
                TotalCount = view.GetTotalCount(),
                StartIndex = startIndex,
                VisibleFieldIndexes = selectedFieldIndexes
            }
        };

        var batch = new List<string?[]>(_options.SnapshotBatchSize);
        foreach (int rowIndex in view.EnumeratePageIndexes(startIndex, pageSize))
        {
            batch.Add(ProjectRow(Collection.GetRowValues(rowIndex), selectedFieldIndexes));
            if (batch.Count == _options.SnapshotBatchSize)
            {
                deltas.Add(CreateSnapshotRowsDelta(viewId, selectedFieldIndexes, batch));
                batch = new List<string?[]>(_options.SnapshotBatchSize);
            }
        }

        if (batch.Count > 0)
        {
            deltas.Add(CreateSnapshotRowsDelta(viewId, selectedFieldIndexes, batch));
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
        List<string?[]> batch)
    {
        return new SnapshotRowsDelta
        {
            ViewId = viewId,
            Schema = Collection.Schema,
            Rows = batch.ToArray(),
            VisibleFieldIndexes = selectedFieldIndexes
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