using System.Collections.Concurrent;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Views;
using Microsoft.Extensions.Logging;

namespace LiveViewEngine.Core;


public interface IViewEngine
{
    Task<IngestResult> IngestAsync(IngestCommand command, CancellationToken ct = default);
    Task<IReadOnlyList<ViewDelta>> SubscribeAsync(SubscriptionCommand command, CancellationToken ct = default);
}

public sealed class ViewEngine(
    ICollectionStore store,
    IOutboundPublisher publisher,
    ILogger<ViewEngine> logger)
    : IViewEngine
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ViewKey, SharedView>> _sharedViewsByCollection = new();
    private readonly ConcurrentDictionary<string, ViewportState> _viewports = new();

    public async Task<IngestResult> IngestAsync(IngestCommand command, CancellationToken ct = default)
    {
        try
        {
            return command switch
            {
                CreateCollectionCommand create => HandleCreateCollection(create),
                UpsertRowCommand upsert => await HandleUpsertAsync(upsert, ct),
                DeleteRowCommand delete => await HandleDeleteAsync(delete, ct),
                _ => IngestResult.Fail($"Unknown command type '{command.GetType().Name}'.")
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing ingest command for collection '{CollectionId}'.",
                command.CollectionId);
            return IngestResult.Fail(ex.Message);
        }
    }

    public Task<IReadOnlyList<ViewDelta>> SubscribeAsync(
        SubscriptionCommand command, CancellationToken ct = default)
    {
        IReadOnlyList<ViewDelta> result = command switch
        {
            SubscribeCommand sub => HandleSubscribe(sub),
            ChangeViewportCommand change => HandleChangeViewport(change),
            UnsubscribeCommand unsub => HandleUnsubscribe(unsub),
            _ => []
        };
        return Task.FromResult(result);
    }

    private IngestResult HandleCreateCollection(CreateCollectionCommand command)
    {
        if (!store.TryCreate(command.Schema))
        {
            return IngestResult.Fail(
                $"Collection '{command.CollectionId}' already exists.");
        }

        logger.LogInformation("Collection '{CollectionId}' created ({FieldCount} fields).",
            command.CollectionId, command.Schema.Fields.Count);
        return IngestResult.Ok();
    }

    private async Task<IngestResult> HandleUpsertAsync(UpsertRowCommand command, CancellationToken ct)
    {
        if (!store.TryGet(command.CollectionId, out var collection) || collection is null)
        {
            return IngestResult.Fail($"Collection '{command.CollectionId}' not found.");
        }

        var mutation = collection.AddOrUpdate(command.Key, command.Fields);
        await PropagateMutationAsync(collection, mutation, isDelete: false, ct);
        return IngestResult.Ok();
    }

    private async Task<IngestResult> HandleDeleteAsync(DeleteRowCommand command, CancellationToken ct)
    {
        if (!store.TryGet(command.CollectionId, out var collection) || collection is null)
        {
            return IngestResult.Fail($"Collection '{command.CollectionId}' not found.");
        }

        var mutation = collection.Delete(command.Key);
        if (mutation is null)
        {
            return IngestResult.Ok();
        }

        await PropagateMutationAsync(collection, mutation, isDelete: true, ct);
        return IngestResult.Ok();
    }

    private async Task PropagateMutationAsync(
        RowCollection collection, MutationInfo mutation, bool isDelete, CancellationToken ct)
    {
        if (!_sharedViewsByCollection.TryGetValue(collection.Schema.CollectionName, out var collectionViews))
        {
            return;
        }

        List<(string ConnectionId, IReadOnlyList<ViewDelta> Deltas)>? pendingPublishes = null;
        foreach (var entry in collectionViews)
        {
            var view = entry.Value;

            bool sortFieldChanged = mutation.IsNew || view.SortFieldTouched(mutation.ChangedColumns);
            bool filterFieldChanged = view.FilterFieldTouched(mutation.ChangedColumns);
            bool needsFullRecompute = isDelete || mutation.IsNew || sortFieldChanged || filterFieldChanged;

            if (isDelete)
            {
                view.NotifyDelete(mutation.Index);
            }
            else if (sortFieldChanged)
            {
                var sortValue = collection.GetValue(mutation.Index, view.SortFieldIndex);
                view.NotifyUpsert(mutation.Index, sortValue);
            }

            if (!needsFullRecompute)
            {
                // Fast path: sort and filter are unaffected — emit RowUpdate only for subscribers
                // whose viewport already contains this row handle.
                if (mutation.ChangedColumns is not { Count: > 0 })
                {
                    continue;
                }

                foreach (var connectionId in view.Subscribers)
                {
                    if (!_viewports.TryGetValue(connectionId, out var viewport))
                    {
                        continue;
                    }

                    int pos = IndexOfHandle(viewport.CurrentHandles, mutation.Index);
                    if (pos < 0)
                    {
                        continue;
                    }

                    pendingPublishes ??= [];
                    pendingPublishes.Add((connectionId, [new RowUpdateDelta
                    {
                        ViewId = view.Key.Id,
                        Schema = collection.Schema,
                        RowId = mutation.RowId,
                        Position = pos,
                        ChangedColumns = mutation.ChangedColumns!
                    }]));
                }

                continue;
            }

            var pageCache = new Dictionary<(int, int), int[]>(4);

            foreach (var connectionId in view.Subscribers)
            {
                if (!_viewports.TryGetValue(connectionId, out var viewport))
                {
                    continue;
                }

                var cacheKey = (viewport.StartIndex, viewport.PageSize);
                if (!pageCache.TryGetValue(cacheKey, out var newHandles))
                {
                    newHandles = view.GetPageIndexes(viewport.StartIndex, viewport.PageSize);
                    pageCache[cacheKey] = newHandles;
                }

                var events = BuildDeltas(view.Key.Id, collection, viewport.CurrentHandles, mutation, isDelete, newHandles);
                if (events.Count == 0)
                {
                    continue;
                }

                viewport.CurrentHandles = newHandles;
                pendingPublishes ??= [];
                pendingPublishes.Add((connectionId, events));
            }
        }

        await PublishAllAsync(pendingPublishes, ct);
    }

    private async Task PublishAllAsync(
        List<(string ConnectionId, IReadOnlyList<ViewDelta> Deltas)>? publishes,
        CancellationToken ct)
    {
        if (publishes is not { Count: > 0 })
        {
            return;
        }

        List<Task>? incomplete = null;
        foreach (var (connectionId, deltas) in publishes)
        {
            var publish = publisher.PublishAsync(connectionId, deltas, ct);
            if (!publish.IsCompletedSuccessfully)
            {
                incomplete ??= new List<Task>(publishes.Count);
                incomplete.Add(publish.AsTask());
            }
        }

        if (incomplete is { Count: > 0 })
        {
            await Task.WhenAll(incomplete);
        }
    }

    private IReadOnlyList<ViewDelta> BuildDeltas(
        string viewId,
        RowCollection collection,
        int[] oldHandles,
        MutationInfo mutation,
        bool isDelete,
        int[] newHandles)
    {
        if (HandlesEqual(newHandles, oldHandles))
        {
            if (isDelete) { return []; }
            return BuildFieldUpdateEvents(viewId, newHandles, mutation, collection);
        }

        var events = new List<ViewDelta>(newHandles.Length + oldHandles.Length);

        for (int i = oldHandles.Length - 1; i >= 0; i--)
        {
            if (!ContainsHandle(newHandles, oldHandles[i]))
            {
                events.Add(new RowRemoveDelta { ViewId = viewId, Position = i });
            }
        }

        for (int i = 0; i < newHandles.Length; i++)
        {
            if (!ContainsHandle(oldHandles, newHandles[i]))
            {
                events.Add(new RowInsertDelta
                {
                    ViewId = viewId,
                    Position = i,
                    Schema = collection.Schema,
                    Row = CopyRow(collection.GetRowValues(newHandles[i]))
                });
            }
        }

        var fieldUpdates = BuildFieldUpdateEvents(viewId, newHandles, mutation, collection);
        events.AddRange(fieldUpdates);

        return events;
    }

    private static IReadOnlyList<ViewDelta> BuildFieldUpdateEvents(
        string viewId,
        int[] handles,
        MutationInfo mutation,
        RowCollection collection)
    {
        if (mutation.IsNew || mutation.ChangedColumns is not { Count: > 0 })
        {
            return [];
        }

        int pos = IndexOfHandle(handles, mutation.Index);
        if (pos < 0) { return []; }

        return [new RowUpdateDelta
        {
            ViewId = viewId,
            Schema = collection.Schema,
            RowId = mutation.RowId,
            Position = pos,
            ChangedColumns = mutation.ChangedColumns
        }];
    }

    private static bool HandlesEqual(int[] first, int[] second)
    {
        if (first.Length != second.Length) { return false; }
        for (int i = 0; i < first.Length; i++)
        {
            if (first[i] != second[i]) { return false; }
        }
        return true;
    }

    private static int IndexOfHandle(int[] handles, int handle)
    {
        for (int i = 0; i < handles.Length; i++)
        {
            if (handles[i] == handle) { return i; }
        }
        return -1;
    }

    private static bool ContainsHandle(int[] handles, int handle) =>
        IndexOfHandle(handles, handle) >= 0;

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

    private IReadOnlyList<ViewDelta> HandleSubscribe(SubscribeCommand command)
    {
        if (!store.TryGet(command.View.CollectionId, out var collection) || collection is null)
        {
            logger.LogWarning("Subscribe failed: collection '{CollectionId}' not found.",
                command.View.CollectionId);
            return [];
        }

        var key = ViewKey.From(command.View);
        var collectionViews = _sharedViewsByCollection.GetOrAdd(
            key.CollectionId, _ => new ConcurrentDictionary<ViewKey, SharedView>());
        var view = collectionViews.GetOrAdd(key, k => new SharedView(k, collection));
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
        viewport.CurrentHandles = indexes;

        logger.LogInformation(
            "Client '{ConnectionId}' subscribed to view '{ViewId}' (start={Start}, page={Page}).",
            command.ConnectionId, key.Id, command.StartIndex, command.PageSize);

        return [new SnapshotDelta
        {
            ViewId = key.Id,
            Schema = collection.Schema,
            TotalCount = view.GetTotalCount(),
            StartIndex = command.StartIndex,
            Rows = BuildRows(collection, indexes)
        }];
    }

    private IReadOnlyList<ViewDelta> HandleChangeViewport(ChangeViewportCommand command)
    {
        if (!_viewports.TryGetValue(command.ConnectionId, out var viewport))
        {
            return [];
        }

        if (!_sharedViewsByCollection.TryGetValue(viewport.ViewKey.CollectionId, out var collectionViews))
        {
            return [];
        }

        if (!collectionViews.TryGetValue(viewport.ViewKey, out var view))
        {
            return [];
        }

        if (!store.TryGet(viewport.ViewKey.CollectionId, out var collection) || collection is null)
        {
            return [];
        }

        viewport.StartIndex = command.StartIndex;
        viewport.PageSize = command.PageSize;

        var indexes = view.GetPageIndexes(command.StartIndex, command.PageSize);
        viewport.CurrentHandles = indexes;

        return [new SnapshotDelta
        {
            ViewId = viewport.ViewKey.Id,
            Schema = collection.Schema,
            TotalCount = view.GetTotalCount(),
            StartIndex = command.StartIndex,
            Rows = BuildRows(collection, indexes)
        }];
    }

    private IReadOnlyList<ViewDelta> HandleUnsubscribe(UnsubscribeCommand command)
    {
        if (!_viewports.TryRemove(command.ConnectionId, out var viewport))
        {
            return [];
        }

        if (_sharedViewsByCollection.TryGetValue(viewport.ViewKey.CollectionId, out var collectionViews)
            && collectionViews.TryGetValue(viewport.ViewKey, out var view))
        {
            view.RemoveSubscriber(command.ConnectionId);
            if (view.IsEmpty)
            {
                collectionViews.TryRemove(viewport.ViewKey, out _);
            }
        }

        logger.LogInformation("Client '{ConnectionId}' unsubscribed.", command.ConnectionId);
        return [];
    }
}
