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
    : IViewEngine, IDisposable
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ViewKey, SharedView>> _sharedViewsByCollection = new();
    private readonly ConcurrentDictionary<string, ViewportState> _viewports = new();
    private readonly SortIndexRegistry _sortIndexRegistry = new();
    private readonly ConcurrentDictionary<string, CollectionState> _states = new();

    public async Task<IngestResult> IngestAsync(IngestCommand command, CancellationToken ct = default)
    {
        try
        {
            if (command is CreateCollectionCommand create)
            {
                return HandleCreateCollection(create);
            }

            if (!_states.TryGetValue(command.CollectionId, out var state))
            {
                return IngestResult.Fail($"Collection '{command.CollectionId}' not found.");
            }

            List<(IReadOnlyList<ViewDelta> Deltas, List<string> ConnectionIds)>? groups;
            IngestResult result;

            await state.Lock.WaitAsync(ct);
            try
            {
                (result, groups) = command switch
                {
                    UpsertRowCommand upsert => HandleUpsert(upsert, state.Propagator),
                    DeleteRowCommand delete => HandleDelete(delete, state.Propagator),
                    _ => (IngestResult.Fail($"Unknown command type '{command.GetType().Name}'."), null)
                };
            }
            finally
            {
                state.Lock.Release();
            }

            if (groups is { Count: > 0 })
            {
                foreach (var (deltas, connectionIds) in groups)
                {
                    await publisher.PublishAsync(connectionIds, deltas, ct);
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error processing ingest command for collection '{CollectionId}'.",
                command.CollectionId);
            return IngestResult.Fail(ex.Message);
        }
    }

    public async Task<IReadOnlyList<ViewDelta>> SubscribeAsync(
        SubscriptionCommand command, CancellationToken ct = default)
    {
        string? collectionId = GetCollectionIdForSubscription(command);

        if (collectionId is null || !_states.TryGetValue(collectionId, out var state))
        {
            return [];
        }

        await state.Lock.WaitAsync(ct);
        try
        {
            return command switch
            {
                SubscribeCommand sub => HandleSubscribe(sub),
                ChangeViewportCommand change => HandleChangeViewport(change),
                UnsubscribeCommand unsub => HandleUnsubscribe(unsub),
                _ => []
            };
        }
        finally
        {
            state.Lock.Release();
        }
    }

    public void Dispose()
    {
        foreach (var state in _states.Values)
        {
            state.Dispose();
        }
    }

    private string? GetCollectionIdForSubscription(SubscriptionCommand command)
    {
        if (command is SubscribeCommand sub)
        {
            return sub.View.CollectionId;
        }

        return _viewports.TryGetValue(command.ConnectionId, out var vp)
            ? vp.ViewKey.CollectionId
            : null;
    }

    private IngestResult HandleCreateCollection(CreateCollectionCommand command)
    {
        if (!store.TryCreate(command.Schema))
        {
            return IngestResult.Fail(
                $"Collection '{command.CollectionId}' already exists.");
        }

        _states.TryAdd(command.CollectionId, new CollectionState());

        logger.LogInformation("Collection '{CollectionId}' created ({FieldCount} fields).",
            command.CollectionId, command.Schema.Fields.Count);
        return IngestResult.Ok();
    }

    private (IngestResult Result, List<(IReadOnlyList<ViewDelta> Deltas, List<string> ConnectionIds)>? Groups)
        HandleUpsert(UpsertRowCommand command, MutationPropagator propagator)
    {
        if (!store.TryGet(command.CollectionId, out var collection) || collection is null)
        {
            return (IngestResult.Fail($"Collection '{command.CollectionId}' not found."), null);
        }

        if (collection.TryGetRowIndex(command.Key, out int existingRowIndex))
        {
            foreach (var sortIndex in _sortIndexRegistry.GetAllForCollection(collection.Schema.CollectionName))
            {
                sortIndex.CaptureOldValue(existingRowIndex);
            }
        }

        var mutation = collection.AddOrUpdate(command.Key, command.Fields);
        List<(IReadOnlyList<ViewDelta>, List<string>)>? groups = null;
        if (_sharedViewsByCollection.TryGetValue(collection.Schema.CollectionName, out var collectionViews))
        {
            groups = propagator.Propagate(collection, collectionViews, _viewports, mutation, isDelete: false);
        }
        return (IngestResult.Ok(), groups);
    }

    private (IngestResult Result, List<(IReadOnlyList<ViewDelta> Deltas, List<string> ConnectionIds)>? Groups)
        HandleDelete(DeleteRowCommand command, MutationPropagator propagator)
    {
        if (!store.TryGet(command.CollectionId, out var collection) || collection is null)
        {
            return (IngestResult.Fail($"Collection '{command.CollectionId}' not found."), null);
        }

        if (collection.TryGetRowIndex(command.Key, out int existingRowIndex))
        {
            foreach (var sortIndex in _sortIndexRegistry.GetAllForCollection(collection.Schema.CollectionName))
            {
                sortIndex.CaptureOldValue(existingRowIndex);
            }
        }

        var mutation = collection.Delete(command.Key);
        if (mutation is null)
        {
            return (IngestResult.Ok(), null);
        }

        List<(IReadOnlyList<ViewDelta>, List<string>)>? groups = null;
        if (_sharedViewsByCollection.TryGetValue(collection.Schema.CollectionName, out var collectionViews))
        {
            groups = propagator.Propagate(collection, collectionViews, _viewports, mutation, isDelete: true);
        }
        return (IngestResult.Ok(), groups);
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
        var sortIndexKey = CreateSortIndexKey(collection, key);
        var sortIndex = _sortIndexRegistry.GetOrCreate(sortIndexKey, collection);
        var view = collectionViews.GetOrAdd(key, k => new SharedView(k, collection, sortIndex));
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
            if (view.IsEmpty && collectionViews.TryRemove(viewport.ViewKey, out _))
            {
                var sortIndexKey = new SortIndexKey(
                    viewport.ViewKey.CollectionId,
                    view.SortIndex.FieldIndex,
                    viewport.ViewKey.SortAscending);
                bool stillUsed = collectionViews.Values.Any(candidate => ReferenceEquals(candidate.SortIndex, view.SortIndex));
                if (!stillUsed)
                {
                    _sortIndexRegistry.Remove(sortIndexKey);
                }
            }
        }

        logger.LogInformation("Client '{ConnectionId}' unsubscribed.", command.ConnectionId);
        return [];
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

    // Holds the per-collection synchronisation primitive and the propagator whose
    // reusable buffers are safe because the semaphore ensures serial access.
    private sealed class CollectionState : IDisposable
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public readonly MutationPropagator Propagator = new();

        public void Dispose() => Lock.Dispose();
    }
}
