using System.Collections.Concurrent;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Views;
using Microsoft.Extensions.Logging;

namespace LiveViewEngine.Core;


public interface IViewEngine
{
    Task<IngestResult> IngestAsync(IngestCommand command, CancellationToken ct = default);
    Task<IReadOnlyList<DeltaEvent>> SubscribeAsync(SubscriptionCommand command, CancellationToken ct = default);
}

public sealed class ViewEngine(ICollectionStore store, IOutboundPublisher publisher, ILogger<ViewEngine> logger)
    : IViewEngine
{
    private readonly ConcurrentDictionary<ViewKey, SharedView> _sharedViews = new();
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

    public Task<IReadOnlyList<DeltaEvent>> SubscribeAsync(
        SubscriptionCommand command, CancellationToken ct = default)
    {
        IReadOnlyList<DeltaEvent> result = command switch
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
        foreach (var entry in _sharedViews)
        {
            var view = entry.Value;
            if (view.Key.CollectionId != collection.Schema.CollectionName)
            {
                continue;
            }

            if (isDelete)
            {
                view.NotifyDelete(mutation.Index);
            }
            else
            {
                var sortValue = collection.GetValue(mutation.Index, view.SortFieldIndex);
                view.NotifyUpsert(mutation.Index, sortValue);
            }

            foreach (var connectionId in view.Subscribers)
            {
                if (!_viewports.TryGetValue(connectionId, out var viewport))
                {
                    continue;
                }

                var newIndexes = view.GetPageIndexes(viewport.StartIndex, viewport.PageSize);
                var newRowIds = BuildRowIds(newIndexes, collection);
                var events = BuildDeltas(
                    view.Key.Id,
                    collection,
                    viewport.CurrentRowIds,
                    mutation,
                    isDelete,
                    newIndexes,
                    newRowIds);
                if (events.Count == 0)
                {
                    continue;
                }

                viewport.CurrentRowIds = newRowIds;
                await publisher.PublishAsync(connectionId, events, ct);
            }
        }
    }

    private IReadOnlyList<DeltaEvent> BuildDeltas(
        string viewId,
        RowCollection collection,
        string[] oldRowIds,
        MutationInfo mutation,
        bool isDelete,
        int[] newIndexes,
        string[] newRowIds)
    {
        if (RowIdsEqual(newRowIds, oldRowIds))
        {
            if (isDelete)
            {
                return [];
            }

            return BuildFieldUpdateEvents(viewId, newRowIds, mutation, collection);
        }

        var events = new List<DeltaEvent>(newRowIds.Length + oldRowIds.Length);
        var newSet = new HashSet<string>(newRowIds);
        var oldSet = new HashSet<string>(oldRowIds);

        for (int i = oldRowIds.Length - 1; i >= 0; i--)
        {
            if (!newSet.Contains(oldRowIds[i]))
            {
                events.Add(new RowRemoveEvent { ViewId = viewId, Position = i });
            }
        }

        for (int i = 0; i < newRowIds.Length; i++)
        {
            if (!oldSet.Contains(newRowIds[i]))
            {
                events.Add(new RowInsertEvent
                {
                    ViewId = viewId,
                    Position = i,
                    Row = collection.GetRow(newIndexes[i])
                });
            }
        }

        var fieldUpdates = BuildFieldUpdateEvents(viewId, newRowIds, mutation, collection);
        events.AddRange(fieldUpdates);

        return events;
    }

    private static IReadOnlyList<DeltaEvent> BuildFieldUpdateEvents(
        string viewId,
        string[] rowIds,
        MutationInfo mutation,
        RowCollection collection)
    {
        if (mutation.IsNew || mutation.ChangedColumns is not { Count: > 0 })
        {
            return [];
        }

        int pos = -1;
        for (int i = 0; i < rowIds.Length; i++)
        {
            if (rowIds[i] == mutation.RowId)
            {
                pos = i;
                break;
            }
        }

        if (pos < 0)
        {
            return [];
        }

        var fields = collection.Schema.Fields;
        var changed = new Dictionary<string, string?>(mutation.ChangedColumns.Count);
        foreach (var (fieldIndex, value) in mutation.ChangedColumns)
        {
            if (fieldIndex < 0 || fieldIndex >= fields.Count)
            {
                continue;
            }

            changed[fields[fieldIndex].Name] = value;
        }

        if (changed.Count == 0)
        {
            return [];
        }

        return [new RowUpdateEvent
        {
            ViewId = viewId,
            RowId = mutation.RowId,
            Position = pos,
            ChangedFields = changed
        }];
    }

    private static bool RowIdsEqual(string[] first, string[] second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        if (first.Length != second.Length)
        {
            return false;
        }

        for (int i = 0; i < first.Length; i++)
        {
            if (first[i] != second[i])
            {
                return false;
            }
        }

        return true;
    }

    private static string[] BuildRowIds(int[] indexes, RowCollection collection)
    {
        var rowIds = new string[indexes.Length];
        for (int i = 0; i < indexes.Length; i++)
        {
            rowIds[i] = collection.GetRowId(indexes[i]) ?? string.Empty;
        }

        return rowIds;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string?>> BuildRows(RowCollection collection, int[] indexes)
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>(indexes.Length);
        for (int i = 0; i < indexes.Length; i++)
        {
            rows.Add(collection.GetRow(indexes[i]));
        }

        return rows;
    }


    private IReadOnlyList<DeltaEvent> HandleSubscribe(SubscribeCommand command)
    {
        if (!store.TryGet(command.View.CollectionId, out var collection) || collection is null)
        {
            logger.LogWarning("Subscribe failed: collection '{CollectionId}' not found.",
                command.View.CollectionId);
            return [];
        }

        var key = ViewKey.From(command.View);
        var view = _sharedViews.GetOrAdd(key, k => new SharedView(k, collection));
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
        viewport.CurrentRowIds = BuildRowIds(indexes, collection);

        logger.LogInformation(
            "Client '{ConnectionId}' subscribed to view '{ViewId}' (start={Start}, page={Page}).",
            command.ConnectionId, key.Id, command.StartIndex, command.PageSize);

        return [new SnapshotEvent
        {
            ViewId = key.Id,
            TotalCount = view.GetTotalCount(),
            StartIndex = command.StartIndex,
            Rows = BuildRows(collection, indexes)
        }];
    }

    private IReadOnlyList<DeltaEvent> HandleChangeViewport(ChangeViewportCommand command)
    {
        if (!_viewports.TryGetValue(command.ConnectionId, out var viewport))
        {
            return [];
        }

        if (!_sharedViews.TryGetValue(viewport.ViewKey, out var view))
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
        viewport.CurrentRowIds = BuildRowIds(indexes, collection);

        return [new SnapshotEvent
        {
            ViewId = viewport.ViewKey.Id,
            TotalCount = view.GetTotalCount(),
            StartIndex = command.StartIndex,
            Rows = BuildRows(collection, indexes)
        }];
    }

    private IReadOnlyList<DeltaEvent> HandleUnsubscribe(UnsubscribeCommand command)
    {
        if (!_viewports.TryRemove(command.ConnectionId, out var viewport))
        {
            return [];
        }

        if (_sharedViews.TryGetValue(viewport.ViewKey, out var view))
        {
            view.RemoveSubscriber(command.ConnectionId);
            if (view.IsEmpty)
            {
                _sharedViews.TryRemove(viewport.ViewKey, out _);
            }
        }

        logger.LogInformation("Client '{ConnectionId}' unsubscribed.", command.ConnectionId);
        return [];
    }
}
