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
    private readonly MutationPropagator _mutationPropagator = new(publisher);

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
        if (_sharedViewsByCollection.TryGetValue(collection.Schema.CollectionName, out var collectionViews))
        {
            await _mutationPropagator.PropagateAsync(
                collection,
                collectionViews,
                _viewports,
                mutation,
                isDelete: false,
                ct);
        }
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

        if (_sharedViewsByCollection.TryGetValue(collection.Schema.CollectionName, out var collectionViews))
        {
            await _mutationPropagator.PropagateAsync(
                collection,
                collectionViews,
                _viewports,
                mutation,
                isDelete: true,
                ct);
        }
        return IngestResult.Ok();
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
        viewport.CurrentRowIndexes = indexes;

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
        viewport.CurrentRowIndexes = indexes;

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
