using System.Collections.Concurrent;

namespace ViewEngineServer.Core;


public interface IViewEngine
{
    Task<IngestResult> IngestAsync(IngestCommand command, CancellationToken ct = default);
    Task<IReadOnlyList<DeltaEvent>> SubscribeAsync(SubscriptionCommand command, CancellationToken ct = default);
}

public sealed class ViewEngine : IViewEngine
{
    private readonly ICollectionStore _store;
    private readonly IOutboundPublisher _publisher;
    private readonly ILogger<ViewEngine> _logger;

    private readonly ConcurrentDictionary<ViewKey, SharedView> _sharedViews = new();

    private readonly ConcurrentDictionary<string, ViewportState> _viewports = new();

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mutationLocks = new();

    public ViewEngine(ICollectionStore store, IOutboundPublisher publisher,
                      ILogger<ViewEngine> logger)
    {
        _store = store;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task<IngestResult> IngestAsync(IngestCommand command, CancellationToken ct = default)
    {
        try
        {
            return command switch
            {
                CreateCollectionCommand create => HandleCreateCollection(create),
                UpsertRowCommand upsert => await HandleUpsertAsync(upsert, ct),
                DeleteRowCommand delete => await HandleDeleteAsync(delete, ct),
                _ => IngestResult.Fail(
                    $"Unknown command type '{command.GetType().Name}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing ingest command for collection '{CollectionId}'.",
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
        if (!_store.TryCreate(command.Schema))
        {
            return IngestResult.Fail(
                $"Collection '{command.CollectionId}' already exists.");
        }

        _logger.LogInformation("Collection '{CollectionId}' created ({FieldCount} fields, capacity {Capacity}).",
            command.CollectionId, command.Schema.Fields.Count, command.Schema.Capacity);
        return IngestResult.Ok();
    }

    private async Task<IngestResult> HandleUpsertAsync(UpsertRowCommand command, CancellationToken ct)
    {
        if (!_store.TryGet(command.CollectionId, out var collection) || collection is null)
        {
            return IngestResult.Fail($"Collection '{command.CollectionId}' not found.");
        }

        var mutation = collection.Upsert(command.Fields);
        await PropagateMutationAsync(collection, mutation, isDelete: false, ct);
        return IngestResult.Ok();
    }

    private async Task<IngestResult> HandleDeleteAsync(DeleteRowCommand command, CancellationToken ct)
    {
        if (!_store.TryGet(command.CollectionId, out var collection) || collection is null)
        {
            return IngestResult.Fail($"Collection '{command.CollectionId}' not found.");
        }

        var mutation = collection.Delete(command.PrimaryKeyValue);
        if (mutation is null)
        {
            return IngestResult.Ok();
        }

        await PropagateMutationAsync(collection, mutation, isDelete: true, ct);
        return IngestResult.Ok();
    }

    private async Task PropagateMutationAsync(
        ColumnarCollection collection, MutationInfo mutation, bool isDelete, CancellationToken ct)
    {
        // Enforce per-collection ordering so every subscriber sees deltas in the same sequence.
        var sem = _mutationLocks.GetOrAdd(collection.Schema.CollectionId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            foreach (var kv in _sharedViews)
            {
                var view = kv.Value;
                if (view.Key.CollectionId != collection.Schema.CollectionId)
                {
                    continue;
                }

                if (isDelete)
                {
                    view.NotifyDelete(mutation.Handle);
                }
                else
                {
                    var sortValue = mutation.NewValues?[view.SortFieldIndex];
                    view.NotifyUpsert(mutation.Handle, sortValue);
                }

                foreach (var connectionId in view.Subscribers)
                {
                    if (!_viewports.TryGetValue(connectionId, out var viewport))
                    {
                        continue;
                    }

                    var events = BuildDeltas(view, collection, viewport, mutation, isDelete);
                    if (events.Count == 0)
                    {
                        continue;
                    }

                    viewport.CurrentRowIds = GetCurrentRowIds(view, viewport, collection);
                    await _publisher.PublishAsync(connectionId, events, ct);
                }
            }
        }
        finally { sem.Release(); }
    }

    private IReadOnlyList<DeltaEvent> BuildDeltas(
        SharedView view,
        ColumnarCollection collection,
        ViewportState viewport,
        MutationInfo mutation,
        bool isDelete)
    {
        var newHandles = view.GetPageHandles(viewport.StartIndex, viewport.PageSize);
        var newRowIds = newHandles.Select(h => collection.GetRowId(h) ?? string.Empty).ToArray();
        var oldRowIds = viewport.CurrentRowIds;

        if (newRowIds.SequenceEqual(oldRowIds))
        {
            if (isDelete)
            {
                return [];
            }

            return BuildFieldUpdateEvents(view.Key.Id, newHandles, newRowIds, mutation, collection);
        }

        var events = new List<DeltaEvent>();
        var newSet = new HashSet<string>(newRowIds);
        var oldSet = new HashSet<string>(oldRowIds);

        for (int i = oldRowIds.Length - 1; i >= 0; i--)
        {
            if (!newSet.Contains(oldRowIds[i]))
            {
                events.Add(new RowRemoveEvent { ViewId = view.Key.Id, Position = i });
            }
        }

        for (int i = 0; i < newRowIds.Length; i++)
        {
            if (!oldSet.Contains(newRowIds[i]))
            {
                events.Add(new RowInsertEvent
                {
                    ViewId = view.Key.Id,
                    Position = i,
                    Row = collection.GetRow(newHandles[i])
                });
            }
        }

        var fieldUpdates = BuildFieldUpdateEvents(view.Key.Id, newHandles, newRowIds, mutation, collection);
        events.AddRange(fieldUpdates);

        return events;
    }

    private static IReadOnlyList<DeltaEvent> BuildFieldUpdateEvents(
        string viewId,
        int[] handles,
        string[] rowIds,
        MutationInfo mutation,
        ColumnarCollection collection)
    {
        if (mutation.IsNew || mutation.PreviousValues is null || mutation.NewValues is null)
        {
            return [];
        }

        int pos = Array.IndexOf(rowIds, mutation.RowId);
        if (pos < 0)
        {
            return [];
        }

        var changed = new Dictionary<string, object?>();
        for (int fi = 0; fi < collection.Schema.Fields.Count; fi++)
        {
            if (!Equals(mutation.PreviousValues[fi], mutation.NewValues[fi]))
            {
                changed[collection.Schema.Fields[fi].Name] = mutation.NewValues[fi];
            }
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

    private static string[] GetCurrentRowIds(
        SharedView view, ViewportState viewport, ColumnarCollection collection)
    {
        var handles = view.GetPageHandles(viewport.StartIndex, viewport.PageSize);
        return handles.Select(h => collection.GetRowId(h) ?? string.Empty).ToArray();
    }


    private IReadOnlyList<DeltaEvent> HandleSubscribe(SubscribeCommand command)
    {
        if (!_store.TryGet(command.View.CollectionId, out var collection) || collection is null)
        {
            _logger.LogWarning("Subscribe failed: collection '{CollectionId}' not found.",
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

        var handles = view.GetPageHandles(command.StartIndex, command.PageSize);
        viewport.CurrentRowIds = handles
            .Select(h => collection.GetRowId(h) ?? string.Empty)
            .ToArray();

        _logger.LogInformation(
            "Client '{ConnectionId}' subscribed to view '{ViewId}' (start={Start}, page={Page}).",
            command.ConnectionId, key.Id, command.StartIndex, command.PageSize);

        return [new SnapshotEvent
        {
            ViewId = key.Id,
            TotalCount = view.GetTotalCount(),
            StartIndex = command.StartIndex,
            Rows = handles.Select(h => collection.GetRow(h)).ToList()
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

        if (!_store.TryGet(viewport.ViewKey.CollectionId, out var collection) || collection is null)
        {
            return [];
        }

        viewport.StartIndex = command.StartIndex;
        viewport.PageSize = command.PageSize;

        var handles = view.GetPageHandles(command.StartIndex, command.PageSize);
        viewport.CurrentRowIds = handles
            .Select(h => collection.GetRowId(h) ?? string.Empty)
            .ToArray();

        return [new SnapshotEvent
        {
            ViewId = viewport.ViewKey.Id,
            TotalCount = view.GetTotalCount(),
            StartIndex = command.StartIndex,
            Rows = handles.Select(h => collection.GetRow(h)).ToList()
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

        _logger.LogInformation("Client '{ConnectionId}' unsubscribed.", command.ConnectionId);
        return [];
    }
}
