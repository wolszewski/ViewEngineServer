using System.Collections.Concurrent;
using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core.Views;

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
            var (oldFilteredPos, newFilteredPos) = ApplyIndexMutation(
                view,
                collection,
                mutation,
                isDelete,
                impact.NeedsFullRecompute);

            if (!impact.NeedsFullRecompute)
            {
                CollectFastPathPublishes(collection, view, viewports, mutation, ref pendingPublishes);
                continue;
            }

            CollectPositionBasedPublishes(
                collection,
                view,
                viewports,
                mutation,
                oldFilteredPos,
                newFilteredPos,
                ref pendingPublishes);
        }

        await PublishAllAsync(pendingPublishes, ct);
    }

    private static MutationImpact AnalyzeMutationImpact(SharedView view, MutationInfo mutation, bool isDelete)
    {
        if (isDelete || mutation.IsNew)
        {
            return new MutationImpact(NeedsFullRecompute: true);
        }

        var (sortFieldTouched, filterFieldChanged) = view.TouchedFields(mutation.ChangedMask);
        return new MutationImpact(NeedsFullRecompute: sortFieldTouched || filterFieldChanged);
    }

    private static (int OldFilteredPos, int NewFilteredPos) ApplyIndexMutation(
        SharedView view,
        RowCollection collection,
        MutationInfo mutation,
        bool isDelete,
        bool needsFullRecompute)
    {
        if (isDelete)
        {
            int oldPos = view.NotifyDelete(mutation.RowIndex);
            return (oldPos, -1);
        }

        if (!needsFullRecompute)
        {
            return (-1, -1);
        }

        var sortValue = collection.GetValue(mutation.RowIndex, view.SortFieldIndex);
        return view.NotifyUpsert(mutation.RowIndex, sortValue, mutation.IsNew);
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

            int filteredPos = view.FilteredIndexOf(mutation.RowIndex);
            if (filteredPos < 0)
            {
                continue;
            }

            int start = viewport.StartIndex;
            int end = viewport.PageSize.HasValue ? start + viewport.PageSize.Value : int.MaxValue;
            if (filteredPos < start || filteredPos >= end)
            {
                continue;
            }

            pendingPublishes ??= [];
            pendingPublishes.Add((connectionId, [new RowUpdateDelta
            {
                ViewId = view.Key.Id,
                Schema = collection.Schema,
                RowId = mutation.RowId,
                Position = filteredPos - start,
                ChangedColumns = mutation.ChangedColumns
            }]));
        }
    }

    private static void CollectPositionBasedPublishes(
        RowCollection collection,
        SharedView view,
        ConcurrentDictionary<string, ViewportState> viewports,
        MutationInfo mutation,
        int oldFilteredPos,
        int newFilteredPos,
        ref List<(string ConnectionId, IReadOnlyList<ViewDelta> Deltas)>? pendingPublishes)
    {
        foreach (var connectionId in view.Subscribers)
        {
            if (!viewports.TryGetValue(connectionId, out var viewport))
            {
                continue;
            }

            var deltas = ComputePositionDeltas(
                view,
                collection,
                view.Key.Id,
                mutation,
                oldFilteredPos,
                newFilteredPos,
                viewport.StartIndex,
                viewport.PageSize);
            if (deltas.Count == 0)
            {
                continue;
            }

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

    private static IReadOnlyList<ViewDelta> ComputePositionDeltas(
        SharedView view,
        RowCollection collection,
        string viewId,
        MutationInfo mutation,
        int oldFilteredPos,
        int newFilteredPos,
        int startIndex,
        int? pageSize)
    {
        if (pageSize is <= 0)
        {
            return [];
        }

        int n = view.FilteredCount;
        int start = Math.Max(0, startIndex);
        bool hasFinitePage;
        int end;
        int bottomPosition;
        if (pageSize is int finitePageSize)
        {
            hasFinitePage = true;
            end = start + finitePageSize;
            bottomPosition = finitePageSize - 1;
        }
        else
        {
            hasFinitePage = false;
            end = int.MaxValue;
            bottomPosition = -1;
        }

        bool oldIn = oldFilteredPos >= start && oldFilteredPos < end;
        bool oldBefore = oldFilteredPos >= 0 && oldFilteredPos < start;
        bool newIn = newFilteredPos >= start && newFilteredPos < end;
        bool newBefore = newFilteredPos >= 0 && newFilteredPos < start;

        if (oldFilteredPos == newFilteredPos)
        {
            return BuildUpdateDeltaIfVisible(viewId, collection, mutation, newIn ? newFilteredPos - start : -1);
        }

        if (!oldIn && !oldBefore && !newIn && !newBefore)
        {
            return [];
        }

        if (oldBefore && newBefore)
        {
            return [];
        }

        var deltas = new List<ViewDelta>(3);

        void AddRemove(int position)
        {
            deltas.Add(new RowRemoveDelta
            {
                ViewId = viewId,
                Position = position
            });
        }

        void AddInsert(int rowIndex, int position)
        {
            deltas.Add(new RowInsertDelta
            {
                ViewId = viewId,
                Position = position,
                Schema = collection.Schema,
                Row = CopyRow(collection.GetRowValues(rowIndex))
            });
        }

        void AddUpdate(int position)
        {
            if (mutation.IsNew || mutation.ChangedColumns is not { Count: > 0 })
            {
                return;
            }

            deltas.Add(new RowUpdateDelta
            {
                ViewId = viewId,
                Schema = collection.Schema,
                RowId = mutation.RowId,
                Position = position,
                ChangedColumns = mutation.ChangedColumns
            });
        }

        bool Exists(int pos) => pos >= 0 && pos < n;
        int BoundaryRow(int pos) => view.GetFilteredByIndex(pos);

        if (oldIn && newIn)
        {
            AddRemove(oldFilteredPos - start);
            AddInsert(mutation.RowIndex, newFilteredPos - start);
        }
        else if (oldBefore && newIn)
        {
            if (Exists(start - 1))
            {
                AddRemove(0);
            }

            AddInsert(mutation.RowIndex, newFilteredPos - start);
        }
        else if (oldIn && newBefore)
        {
            AddRemove(oldFilteredPos - start);
            if (Exists(start))
            {
                AddInsert(BoundaryRow(start), 0);
            }
        }
        else if (!oldIn && !oldBefore && newIn)
        {
            AddInsert(mutation.RowIndex, newFilteredPos - start);
            if (hasFinitePage && Exists(end))
            {
                AddRemove(bottomPosition);
            }
        }
        else if (oldIn && !newIn && !newBefore)
        {
            AddRemove(oldFilteredPos - start);
            if (hasFinitePage && Exists(end - 1))
            {
                AddInsert(BoundaryRow(end - 1), bottomPosition);
            }
        }
        else if (!oldIn && !oldBefore && newBefore)
        {
            if (Exists(start))
            {
                AddInsert(BoundaryRow(start), 0);
            }

            if (hasFinitePage && Exists(end))
            {
                AddRemove(bottomPosition);
            }
        }
        else if (oldBefore && !newIn && !newBefore)
        {
            if (Exists(start - 1))
            {
                AddRemove(0);
            }

            if (hasFinitePage && Exists(end - 1))
            {
                AddInsert(BoundaryRow(end - 1), bottomPosition);
            }
        }

        if (newIn)
        {
            AddUpdate(newFilteredPos - start);
        }

        return deltas;
    }

    private static IReadOnlyList<ViewDelta> BuildUpdateDeltaIfVisible(
        string viewId,
        RowCollection collection,
        MutationInfo mutation,
        int position)
    {
        if (position < 0 || mutation.IsNew || mutation.ChangedColumns is not { Count: > 0 })
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

    private static string?[] CopyRow(string?[] source)
    {
        var copy = new string?[source.Length];
        Array.Copy(source, copy, source.Length);
        return copy;
    }

    private readonly record struct MutationImpact(bool NeedsFullRecompute);
}
