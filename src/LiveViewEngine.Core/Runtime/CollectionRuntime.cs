using System.Collections.Concurrent;
using System.Diagnostics;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Views;

namespace LiveViewEngine.Core.Runtime;

public sealed class CollectionRuntime : IDisposable
{
    private static readonly TimeSpan StaleIndexGracePeriod = TimeSpan.FromSeconds(30);
    private readonly CollectionWorker _worker = new();
    private readonly ConcurrentDictionary<ViewKey, SharedView> _sharedViews = new();
    private readonly ConcurrentDictionary<string, ViewportState> _viewports = new();
    private readonly SortIndexRegistry _sortIndexRegistry = new();
    private readonly IViewEngineMetrics? _metrics;

    public CollectionRuntime(RowCollection collection, IViewEngineMetrics? metrics)
    {
        Collection = collection;
        _metrics = metrics;
        _worker.Start();
    }

    public RowCollection Collection { get; }
    private readonly MutationPropagator _propagator = new();

    public int ActiveSubscriptionCount => _viewports.Count;
    public int ActiveSharedViewCount => _sharedViews.Count;
    public int SortIndexCount => _sortIndexRegistry.Count;

    public Task<T> EnqueueAsync<T>(IWorkItem<T> work, CancellationToken ct = default) =>
        _worker.EnqueueAsync(work, ct);

    public bool TryGetCollectionIdForConnection(string connectionId, out string? collectionId)
    {
        collectionId = _viewports.TryGetValue(connectionId, out var viewport) ? viewport.ViewKey.CollectionId : null;
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
            List<(IReadOnlyList<ViewDelta>, List<string>)>? groups = null;
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
                _metrics.UpdateDuration.Record(
                    durationMs,
                    new KeyValuePair<string, object?>("collectionId", Collection.Schema.CollectionName));
                _metrics.UpdateCount.Add(
                    1,
                    new KeyValuePair<string, object?>("collectionId", Collection.Schema.CollectionName));
            }
            else
            {
                _metrics.InsertDuration.Record(
                    durationMs,
                    new KeyValuePair<string, object?>("collectionId", Collection.Schema.CollectionName));
                _metrics.InsertCount.Add(
                    1,
                    new KeyValuePair<string, object?>("collectionId", Collection.Schema.CollectionName));
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

        List<(IReadOnlyList<ViewDelta>, List<string>)>? groups = null;
        if (_sharedViews.Count > 0)
        {
            groups = _propagator.Propagate(Collection, _sharedViews, _viewports, mutation, isDelete: true);
        }

        return new MutationResult(IngestResult.Ok(), groups);
    }

    public IReadOnlyList<ViewDelta> HandleSubscribe(SubscribeCommand command)
    {
        var key = ViewKey.From(command.View);
        var sortIndexKey = CreateSortIndexKey(Collection, key);
        var sortIndex = _sortIndexRegistry.GetOrCreate(sortIndexKey, Collection);
        _sortIndexRegistry.UnflagForRemoval(sortIndexKey);
        var view = _sharedViews.GetOrAdd(key, k => new SharedView(k, Collection, sortIndex));
        view.AddSubscriber(command.ConnectionId);
        sortIndex.IncrementSubscribers();

        var selectedFieldIndexes = ResolveVisibleFieldIndexes(command.View);
        var viewport = new ViewportState
        {
            ConnectionId = command.ConnectionId,
            ViewKey = key,
            StartIndex = command.StartIndex,
            PageSize = command.PageSize,
            VisibleColumns = FieldMask.From(selectedFieldIndexes.AsSpan()),
            SelectedFieldIndexes = selectedFieldIndexes
        };
        _viewports[command.ConnectionId] = viewport;

        var indexes = view.GetPageIndexes(command.StartIndex, command.PageSize);
        return
        [
            new SnapshotDelta
            {
                ViewId = key.Id,
                Schema = Collection.Schema,
                TotalCount = view.GetTotalCount(),
                StartIndex = command.StartIndex,
                Rows = BuildRows(Collection, indexes, selectedFieldIndexes),
                VisibleFieldIndexes = selectedFieldIndexes
            }
        ];
    }

    public IReadOnlyList<ViewDelta> HandleChangeViewport(ChangeViewportCommand command)
    {
        if (!_viewports.TryGetValue(command.ConnectionId, out var viewport))
        {
            return [];
        }

        if (!_sharedViews.TryGetValue(viewport.ViewKey, out var view))
        {
            return [];
        }

        viewport.StartIndex = command.StartIndex;
        viewport.PageSize = command.PageSize;

        var indexes = view.GetPageIndexes(command.StartIndex, command.PageSize);
        return
        [
            new SnapshotDelta
            {
                ViewId = viewport.ViewKey.Id,
                Schema = Collection.Schema,
                TotalCount = view.GetTotalCount(),
                StartIndex = command.StartIndex,
                Rows = BuildRows(Collection, indexes, viewport.SelectedFieldIndexes),
                VisibleFieldIndexes = viewport.SelectedFieldIndexes
            }
        ];
    }

    public IReadOnlyList<ViewDelta> HandleUnsubscribe(UnsubscribeCommand command)
    {
        if (!_viewports.TryRemove(command.ConnectionId, out var viewport))
        {
            return [];
        }

        if (_sharedViews.TryGetValue(viewport.ViewKey, out var view))
        {
            view.RemoveSubscriber(command.ConnectionId);
            var sortIndex = view.SortIndex;
            sortIndex.DecrementSubscribers();

            if (view.IsEmpty)
            {
                _sharedViews.TryRemove(viewport.ViewKey, out _);
            }

            if (sortIndex.SubscriberCount == 0)
            {
                var sortIndexKey = new SortIndexKey(
                    viewport.ViewKey.CollectionId,
                    sortIndex.FieldIndex,
                    viewport.ViewKey.SortAscending);
                _sortIndexRegistry.FlagForRemoval(sortIndexKey);
            }
        }

        return [];
    }

    public void Dispose() => _worker.Dispose();

    internal async Task ReapOnceAsync(CancellationToken ct)
    {
        foreach (var (key, flaggedAt) in _sortIndexRegistry.GetFlagged())
        {
            if (DateTime.UtcNow - flaggedAt >= StaleIndexGracePeriod)
            {
                await EnqueueAsync(new RemoveStaleIndexRuntimeWork(this, key), ct).ConfigureAwait(false);
            }
        }
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

        return new SortIndexKey(key.CollectionId, sortFieldIndex, key.SortAscending);
    }

    private int[] ResolveVisibleFieldIndexes(ViewDefinition view)
    {
        if (view.Fields is null)
        {
            return Enumerable.Range(0, Collection.Schema.Fields.Count).ToArray();
        }

        var keyFieldName = Collection.Schema.Fields[0].Name;
        var includeKey = !view.Fields.Any(f => string.Equals(f, keyFieldName, StringComparison.OrdinalIgnoreCase));
        var indexes = new List<int>(view.Fields.Count + (includeKey ? 1 : 0));
        if (includeKey)
        {
            indexes.Add(0);
        }

        foreach (var fieldName in view.Fields)
        {
            int fieldIndex = Collection.Schema.GetFieldIndex(fieldName);
            if (fieldIndex < 0)
            {
                throw new ArgumentException(
                    $"Unknown field '{fieldName}' for collection '{Collection.Schema.CollectionName}'.",
                    nameof(view.Fields));
            }

            indexes.Add(fieldIndex);
        }

        return indexes.ToArray();
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