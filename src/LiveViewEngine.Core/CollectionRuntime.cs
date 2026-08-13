using System.Collections.Concurrent;
using System.Diagnostics;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Views;

namespace LiveViewEngine.Core;

public sealed class CollectionRuntime : IDisposable
{
    private readonly CollectionWorker _channel = new();
    private readonly ConcurrentDictionary<ViewKey, SharedView> _sharedViews = new();
    private readonly ConcurrentDictionary<string, ViewportState> _viewports = new();
    private readonly SortIndexRegistry _sortIndexRegistry = new();

    public CollectionRuntime(string collectionId, RowCollection collection)
    {
        CollectionId = collectionId;
        Collection = collection;
        _channel.Start();
    }

    public string CollectionId { get; }
    public RowCollection Collection { get; }
    public MutationPropagator Propagator { get; } = new();

    public Task<T> EnqueueAsync<T>(Func<T> work, CancellationToken ct = default) =>
        _channel.EnqueueAsync(work, ct);

    public bool TryGetCollectionIdForConnection(string connectionId, out string? collectionId)
    {
        collectionId = _viewports.TryGetValue(connectionId, out var viewport) ? viewport.ViewKey.CollectionId : null;
        return collectionId is not null;
    }

    public (IngestResult Result, List<(IReadOnlyList<ViewDelta> Deltas, List<string> ConnectionIds)>? Groups)
        HandleUpsert(UpsertRowCommand command)
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
                groups = Propagator.Propagate(Collection, _sharedViews, _viewports, mutation, isDelete: false);
            }

            return (IngestResult.Ok(), groups);
        }
        finally
        {
            var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (rowAlreadyExisted)
            {
                ViewEngineMetrics.UpdateDuration.Record(
                    durationMs,
                    new KeyValuePair<string, object?>("collectionId", CollectionId));
            }
            else
            {
                ViewEngineMetrics.InsertDuration.Record(
                    durationMs,
                    new KeyValuePair<string, object?>("collectionId", CollectionId));
            }
        }
    }

    public (IngestResult Result, List<(IReadOnlyList<ViewDelta> Deltas, List<string> ConnectionIds)>? Groups)
        HandleDelete(DeleteRowCommand command)
    {
        if (Collection.TryGetRowIndex(command.Key, out int existingRowIndex))
        {
            foreach (var sortIndex in _sortIndexRegistry.GetAllForCollection(Collection.Schema.CollectionName))
            {
                sortIndex.CaptureOldValue(existingRowIndex);
            }
        }

        var mutation = Collection.Delete(command.Key);
        if (mutation is null)
        {
            return (IngestResult.Ok(), null);
        }

        List<(IReadOnlyList<ViewDelta>, List<string>)>? groups = null;
        if (_sharedViews.Count > 0)
        {
            groups = Propagator.Propagate(Collection, _sharedViews, _viewports, mutation, isDelete: true);
        }

        return (IngestResult.Ok(), groups);
    }

    public IReadOnlyList<ViewDelta> HandleSubscribe(SubscribeCommand command)
    {
        var key = ViewKey.From(command.View);
        var sortIndexKey = CreateSortIndexKey(Collection, key);
        var sortIndex = _sortIndexRegistry.GetOrCreate(sortIndexKey, Collection);
        var view = _sharedViews.GetOrAdd(key, k => new SharedView(k, Collection, sortIndex));
        view.AddSubscriber(command.ConnectionId);

        var viewport = new ViewportState
        {
            ConnectionId = command.ConnectionId,
            ViewKey = key,
            StartIndex = command.StartIndex,
            PageSize = command.PageSize
        };
        _viewports[command.ConnectionId] = viewport;

        var indexes = view.GetPageIndexes(command.StartIndex, command.PageSize);
        return [new SnapshotDelta
        {
            ViewId = key.Id,
            Schema = Collection.Schema,
            TotalCount = view.GetTotalCount(),
            StartIndex = command.StartIndex,
            Rows = BuildRows(Collection, indexes)
        }];
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
        return [new SnapshotDelta
        {
            ViewId = viewport.ViewKey.Id,
            Schema = Collection.Schema,
            TotalCount = view.GetTotalCount(),
            StartIndex = command.StartIndex,
            Rows = BuildRows(Collection, indexes)
        }];
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
            if (view.IsEmpty && _sharedViews.TryRemove(viewport.ViewKey, out _))
            {
                var sortIndexKey = new SortIndexKey(
                    viewport.ViewKey.CollectionId,
                    view.SortIndex.FieldIndex,
                    viewport.ViewKey.SortAscending);
                bool stillUsed = _sharedViews.Values.Any(candidate => ReferenceEquals(candidate.SortIndex, view.SortIndex));
                if (!stillUsed)
                {
                    _sortIndexRegistry.Remove(sortIndexKey);
                }
            }
        }

        return [];
    }

    public void Dispose() => _channel.Dispose();

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

    private static IReadOnlyList<string?[]> BuildRows(RowCollection collection, int[] indexes)
    {
        var rows = new string?[indexes.Length][];
        for (int i = 0; i < indexes.Length; i++)
        {
            rows[i] = CopyRow(collection.GetRowValues(indexes[i]));
        }
        return rows;
    }

    private static string?[] CopyRow(string?[] source)
    {
        var copy = new string?[source.Length];
        Array.Copy(source, copy, source.Length);
        return copy;
    }
}
