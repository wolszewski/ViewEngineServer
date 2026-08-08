using System.Collections.Concurrent;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Views;

namespace LiveViewEngine.Core;

public sealed class MutationPropagator(IOutboundPublisher publisher)
{
    public async Task PropagateAsync(
        RowCollection collection,
        ConcurrentDictionary<ViewKey, SharedView> collectionViews,
        ConcurrentDictionary<string, ViewportState> viewports,
        MutationInfo mutation,
        bool isDelete,
        CancellationToken ct = default)
    {
        List<(string ConnectionId, IReadOnlyList<ViewDelta> Deltas)>? pendingPublishes = null;
        foreach (var entry in collectionViews)
        {
            var view = entry.Value;
            var impact = AnalyzeMutationImpact(view, mutation, isDelete);

            ApplyIndexMutation(view, collection, mutation, isDelete, impact.SortFieldChanged);

            if (!impact.NeedsFullRecompute)
            {
                CollectFastPathPublishes(collection, view, viewports, mutation, ref pendingPublishes);
                continue;
            }

            CollectRecomputedPublishes(collection, view, viewports, mutation, isDelete, ref pendingPublishes);
        }

        await PublishAllAsync(pendingPublishes, ct);
    }

    private static MutationImpact AnalyzeMutationImpact(SharedView view, MutationInfo mutation, bool isDelete)
    {
        var (sortFieldTouched, filterFieldChanged) = view.TouchedFields(mutation.ChangedColumns);
        bool sortFieldChanged = mutation.IsNew || sortFieldTouched;
        bool needsFullRecompute = isDelete || mutation.IsNew || sortFieldChanged || filterFieldChanged;
        return new MutationImpact(sortFieldChanged, needsFullRecompute);
    }

    private static void ApplyIndexMutation(
        SharedView view,
        RowCollection collection,
        MutationInfo mutation,
        bool isDelete,
        bool sortFieldChanged)
    {
        if (isDelete)
        {
            view.NotifyDelete(mutation.RowIndex);
            return;
        }

        if (sortFieldChanged)
        {
            var sortValue = collection.GetValue(mutation.RowIndex, view.SortFieldIndex);
            view.NotifyUpsert(mutation.RowIndex, sortValue);
        }
    }

    private static void CollectFastPathPublishes(
        RowCollection collection,
        SharedView view,
        ConcurrentDictionary<string, ViewportState> viewports,
        MutationInfo mutation,
        ref List<(string ConnectionId, IReadOnlyList<ViewDelta> Deltas)>? pendingPublishes)
    {
        if (mutation.ChangedColumns is not { Count: > 0 })
        {
            return;
        }

        foreach (var connectionId in view.Subscribers)
        {
            if (!viewports.TryGetValue(connectionId, out var viewport))
            {
                continue;
            }

            int position = IndexOfHandle(viewport.CurrentHandles, mutation.RowIndex);
            if (position < 0)
            {
                continue;
            }

            pendingPublishes ??= [];
            pendingPublishes.Add((connectionId, [new RowUpdateDelta
            {
                ViewId = view.Key.Id,
                Schema = collection.Schema,
                RowId = mutation.RowId,
                Position = position,
                ChangedColumns = mutation.ChangedColumns
            }]));
        }
    }

    private static void CollectRecomputedPublishes(
        RowCollection collection,
        SharedView view,
        ConcurrentDictionary<string, ViewportState> viewports,
        MutationInfo mutation,
        bool isDelete,
        ref List<(string ConnectionId, IReadOnlyList<ViewDelta> Deltas)>? pendingPublishes)
    {
        var pageCache = new Dictionary<(int StartIndex, int PageSize), int[]>(4);
        foreach (var connectionId in view.Subscribers)
        {
            if (!viewports.TryGetValue(connectionId, out var viewport))
            {
                continue;
            }

            var cacheKey = (viewport.StartIndex, viewport.PageSize);
            if (!pageCache.TryGetValue(cacheKey, out var newHandles))
            {
                newHandles = view.GetPageIndexes(viewport.StartIndex, viewport.PageSize);
                pageCache[cacheKey] = newHandles;
            }

            var deltas = BuildDeltas(view.Key.Id, collection, viewport.CurrentHandles, mutation, isDelete, newHandles);
            if (deltas.Count == 0)
            {
                continue;
            }

            viewport.CurrentHandles = newHandles;
            pendingPublishes ??= [];
            pendingPublishes.Add((connectionId, deltas));
        }
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

    private static IReadOnlyList<ViewDelta> BuildDeltas(
        string viewId,
        RowCollection collection,
        int[] oldHandles,
        MutationInfo mutation,
        bool isDelete,
        int[] newHandles)
    {
        if (HandlesEqual(newHandles, oldHandles))
        {
            if (isDelete)
            {
                return [];
            }

            return BuildFieldUpdateDeltas(viewId, newHandles, mutation, collection);
        }

        var deltas = new List<ViewDelta>(newHandles.Length + oldHandles.Length);
        BuildRemovalDeltas(deltas, viewId, oldHandles, newHandles);
        BuildInsertionDeltas(deltas, viewId, collection, oldHandles, newHandles);

        if (!mutation.IsNew && mutation.ChangedColumns is { Count: > 0 })
        {
            int position = IndexOfHandle(newHandles, mutation.RowIndex);
            if (position >= 0)
            {
                deltas.Add(new RowUpdateDelta
                {
                    ViewId = viewId,
                    Schema = collection.Schema,
                    RowId = mutation.RowId,
                    Position = position,
                    ChangedColumns = mutation.ChangedColumns
                });
            }
        }

        return deltas;
    }

    private static void BuildRemovalDeltas(List<ViewDelta> deltas, string viewId, int[] oldHandles, int[] newHandles)
    {
        var newSet = new HashSet<int>(newHandles);
        for (int i = oldHandles.Length - 1; i >= 0; i--)
        {
            if (!newSet.Contains(oldHandles[i]))
            {
                deltas.Add(new RowRemoveDelta { ViewId = viewId, Position = i });
            }
        }
    }

    private static void BuildInsertionDeltas(
        List<ViewDelta> deltas,
        string viewId,
        RowCollection collection,
        int[] oldHandles,
        int[] newHandles)
    {
        var oldSet = new HashSet<int>(oldHandles);
        for (int i = 0; i < newHandles.Length; i++)
        {
            if (!oldSet.Contains(newHandles[i]))
            {
                deltas.Add(new RowInsertDelta
                {
                    ViewId = viewId,
                    Position = i,
                    Schema = collection.Schema,
                    Row = CopyRow(collection.GetRowValues(newHandles[i]))
                });
            }
        }
    }

    private static IReadOnlyList<ViewDelta> BuildFieldUpdateDeltas(
        string viewId,
        int[] handles,
        MutationInfo mutation,
        RowCollection collection)
    {
        if (mutation.IsNew || mutation.ChangedColumns is not { Count: > 0 })
        {
            return [];
        }

        int position = IndexOfHandle(handles, mutation.RowIndex);
        if (position < 0)
        {
            return [];
        }

        return [new RowUpdateDelta
        {
            ViewId = viewId,
            Schema = collection.Schema,
            RowId = mutation.RowId,
            Position = position,
            ChangedColumns = mutation.ChangedColumns
        }];
    }

    private static bool HandlesEqual(int[] first, int[] second)
    {
        return first.AsSpan().SequenceEqual(second.AsSpan());
    }

    private static int IndexOfHandle(int[] handles, int handle)
    {
        for (int i = 0; i < handles.Length; i++)
        {
            if (handles[i] == handle)
            {
                return i;
            }
        }

        return -1;
    }

    private static string?[] CopyRow(string?[] source)
    {
        var copy = new string?[source.Length];
        Array.Copy(source, copy, source.Length);
        return copy;
    }

    private readonly record struct MutationImpact(bool SortFieldChanged, bool NeedsFullRecompute);
}
