using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core.Views;

public sealed class MutationPropagator
{
    // Reusable per-propagation buffers — safe because Propagate is called serially per collection.
    private readonly Dictionary<SortIndex, List<SharedView>> _groupBuffer =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<MutationImpact> _impactBuffer = [];
    private readonly List<int> _oldPosBuffer = [];

    // Returns groups of (deltas, connectionIds) where all connectionIds in a group share the
    // same viewport and therefore receive the same delta payload — serialize once, fan out.
    public List<(IReadOnlyList<ViewDelta> Deltas, List<string> ConnectionIds)>? Propagate(
        RowCollection collection,
        ConcurrentDictionary<ViewKey, SharedView> collectionViews,
        ConcurrentDictionary<string, ViewportState> viewports,
        MutationInfo mutation,
        bool isDelete)
    {
        List<(IReadOnlyList<ViewDelta> Deltas, List<string> ConnectionIds)>? pending = null;

        foreach (var entry in collectionViews)
        {
            var sortIndex = entry.Value.SortIndex;
            if (!_groupBuffer.TryGetValue(sortIndex, out var list))
            {
                list = [];
                _groupBuffer[sortIndex] = list;
            }
            list.Add(entry.Value);
        }

        foreach (var (sortIndex, views) in _groupBuffer)
        {
            _impactBuffer.Clear();
            _oldPosBuffer.Clear();
            for (int i = 0; i < views.Count; i++)
            {
                _impactBuffer.Add(AnalyzeMutationImpact(views[i], mutation, isDelete));
                _oldPosBuffer.Add(0);
            }

            // Phase 1: capture old filtered positions before the SortIndex tree mutates.
            for (int i = 0; i < views.Count; i++)
            {
                if (!_impactBuffer[i].NeedsFullRecompute) { continue; }
                _oldPosBuffer[i] = isDelete
                    ? views[i].PrepareDelete(mutation.RowIndex)
                    : views[i].PrepareUpsert(mutation.RowIndex, mutation.IsNew);
            }

            // Phase 2: update SortIndex once.
            if (isDelete)
            {
                sortIndex.OnDelete(mutation.RowIndex);
            }
            else if (mutation.IsNew || AnySortFieldTouched(_impactBuffer))
            {
                sortIndex.OnUpsert(mutation.RowIndex);
            }
            else
            {
                sortIndex.ResetPending();
            }

            // Phase 3: complete filtered index updates and collect grouped deltas.
            for (int i = 0; i < views.Count; i++)
            {
                var view = views[i];
                if (!_impactBuffer[i].NeedsFullRecompute)
                {
                    CollectFastPathGroups(collection, view, viewports, mutation, ref pending);
                    continue;
                }

                int newFilteredPos = isDelete ? -1 : views[i].CompleteUpsert(mutation.RowIndex);
                CollectPositionGroups(
                    collection, view, viewports, mutation,
                    _oldPosBuffer[i], newFilteredPos,
                    isDelete,
                    ref pending);
            }

            views.Clear();
        }

        _groupBuffer.Clear();
        return pending;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AnySortFieldTouched(List<MutationImpact> impacts)
    {
        for (int i = 0; i < impacts.Count; i++)
        {
            if (impacts[i].SortFieldTouched) { return true; }
        }
        return false;
    }

    private static MutationImpact AnalyzeMutationImpact(SharedView view, MutationInfo mutation, bool isDelete)
    {
        if (isDelete || mutation.IsNew)
        {
            return new MutationImpact(NeedsFullRecompute: true, SortFieldTouched: true);
        }

        var (sortFieldTouched, filterFieldChanged) = view.TouchedFields(mutation.ChangedMask);
        return new MutationImpact(
            NeedsFullRecompute: sortFieldTouched || filterFieldChanged,
            SortFieldTouched: sortFieldTouched);
    }

    // Fast path: field update that doesn't affect sort order or filter membership.
    // Subscribers with the same viewport see the row at the same position — share one RowUpdateDelta.
    private static void CollectFastPathGroups(
        RowCollection collection,
        SharedView view,
        ConcurrentDictionary<string, ViewportState> viewports,
        MutationInfo mutation,
        ref List<(IReadOnlyList<ViewDelta>, List<string>)>? pending)
    {
        if (mutation.ChangedColumns is not { Count: > 0 })
        {
            return;
        }

        int filteredPos = view.FilteredIndexOf(mutation.RowIndex);
        if (filteredPos < 0)
        {
            return;
        }

        Dictionary<(int Start, int? PageSize, FieldMask VisibleColumns), List<string>>? groups = null;
        foreach (var connectionId in view.Subscribers)
        {
            if (!viewports.TryGetValue(connectionId, out var viewport))
            {
                continue;
            }

            if (!viewport.VisibleColumns.Intersects(mutation.ChangedMask))
            {
                continue;
            }

            int start = viewport.StartIndex;
            int end = viewport.PageSize.HasValue ? start + viewport.PageSize.Value : int.MaxValue;
            if (filteredPos < start || filteredPos >= end)
            {
                continue;
            }

            groups ??= new();
            var key = (start, viewport.PageSize, viewport.VisibleColumns);
            if (!groups.TryGetValue(key, out var list)) { list = []; groups[key] = list; }
            list.Add(connectionId);
        }

        if (groups is null) { return; }

        foreach (var ((start, _, _), connIds) in groups)
        {
            pending ??= [];
            var viewport = viewports[connIds[0]];
            var projectedColumns = FilterChangedColumns(mutation.ChangedColumns, viewport.VisibleColumns);
            if (projectedColumns.Count == 0)
            {
                continue;
            }

            pending.Add(([new RowUpdateDelta
            {
                ViewId = view.Key.Id,
                Schema = collection.Schema,
                RowId = mutation.RowId,
                Position = filteredPos - start,
                ChangedColumns = projectedColumns,
                VisibleFieldIndexes = viewport.SelectedFieldIndexes
            }], connIds));
        }
    }

    // Full-recompute path: new row, delete, sort-field change, or filter-field change.
    // Subscribers with the same viewport and projection produce the same delta sequence — compute and share.
    private static void CollectPositionGroups(
        RowCollection collection,
        SharedView view,
        ConcurrentDictionary<string, ViewportState> viewports,
        MutationInfo mutation,
        int oldFilteredPos,
        int newFilteredPos,
        bool isDelete,
        ref List<(IReadOnlyList<ViewDelta>, List<string>)>? pending)
    {
        Dictionary<(int Start, int? PageSize, FieldMask VisibleColumns), List<string>>? groups = null;
        foreach (var connectionId in view.Subscribers)
        {
            if (!viewports.TryGetValue(connectionId, out var viewport))
            {
                continue;
            }

            groups ??= new();
            var key = (viewport.StartIndex, viewport.PageSize, viewport.VisibleColumns);
            if (!groups.TryGetValue(key, out var list)) { list = []; groups[key] = list; }
            list.Add(connectionId);
        }

        if (groups is null) { return; }

        foreach (var ((start, pageSize, projectionKey), connIds) in groups)
        {
            var visibleViewport = viewports[connIds[0]];
            var deltas = ComputePositionDeltas(
                view, collection, view.Key.Id, mutation,
                oldFilteredPos, newFilteredPos, start, pageSize,
                visibleViewport.SelectedFieldIndexes, visibleViewport.VisibleColumns, isDelete);
            if (deltas.Count == 0) { continue; }

            pending ??= [];
            pending.Add((deltas, connIds));
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
        int? pageSize,
        int[] selectedFieldIndexes,
        FieldMask visibleMask,
        bool isDelete)
    {
        if (pageSize is <= 0)
        {
            return [];
        }

        if (!isDelete && !mutation.IsNew && !visibleMask.Intersects(mutation.ChangedMask))
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
            return BuildUpdateDeltaIfVisible(
                viewId, collection, mutation, newIn ? newFilteredPos - start : -1,
                selectedFieldIndexes, visibleMask, isDelete);
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
            deltas.Add(new RowRemoveDelta { ViewId = viewId, Position = position });
        }

        void AddInsert(int rowIndex, int position)
        {
            var row = ProjectRow(collection.GetRowValues(rowIndex), selectedFieldIndexes);
            deltas.Add(new RowInsertDelta
            {
                ViewId = viewId,
                Position = position,
                Schema = collection.Schema,
                Row = row,
                VisibleFieldIndexes = selectedFieldIndexes
            });
        }

        void AddUpdate(int position)
        {
            if (mutation.IsNew || mutation.ChangedColumns is not { Count: > 0 }) { return; }
            var projected = FilterChangedColumns(mutation.ChangedColumns, visibleMask);
            if (projected.Count == 0) { return; }

            deltas.Add(new RowUpdateDelta
            {
                ViewId = viewId,
                Schema = collection.Schema,
                RowId = mutation.RowId,
                Position = position,
                ChangedColumns = projected,
                VisibleFieldIndexes = selectedFieldIndexes
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
            if (Exists(start - 1)) { AddRemove(0); }
            AddInsert(mutation.RowIndex, newFilteredPos - start);
        }
        else if (oldIn && newBefore)
        {
            AddRemove(oldFilteredPos - start);
            if (Exists(start)) { AddInsert(BoundaryRow(start), 0); }
        }
        else if (!oldIn && !oldBefore && newIn)
        {
            AddInsert(mutation.RowIndex, newFilteredPos - start);
            if (hasFinitePage && Exists(end)) { AddRemove(bottomPosition); }
        }
        else if (oldIn && !newIn && !newBefore)
        {
            AddRemove(oldFilteredPos - start);
            if (hasFinitePage && Exists(end - 1)) { AddInsert(BoundaryRow(end - 1), bottomPosition); }
        }
        else if (!oldIn && !oldBefore && newBefore)
        {
            if (Exists(start)) { AddInsert(BoundaryRow(start), 0); }
            if (hasFinitePage && Exists(end)) { AddRemove(bottomPosition); }
        }
        else if (oldBefore && !newIn && !newBefore)
        {
            if (Exists(start - 1)) { AddRemove(0); }
            if (hasFinitePage && Exists(end - 1)) { AddInsert(BoundaryRow(end - 1), bottomPosition); }
        }

        if (newIn) { AddUpdate(newFilteredPos - start); }

        return deltas;
    }

    private static IReadOnlyList<ViewDelta> BuildUpdateDeltaIfVisible(
        string viewId,
        RowCollection collection,
        MutationInfo mutation,
        int position,
        int[] selectedFieldIndexes,
        FieldMask visibleMask,
        bool isDelete)
    {
        if (position < 0 || mutation.IsNew || mutation.ChangedColumns is not { Count: > 0 })
        {
            return [];
        }

        if (isDelete || !visibleMask.Intersects(mutation.ChangedMask))
        {
            return [];
        }

        var projectedColumns = FilterChangedColumns(mutation.ChangedColumns, visibleMask);
        if (projectedColumns.Count == 0)
        {
            return [];
        }

        return [new RowUpdateDelta
        {
            ViewId = viewId,
            Schema = collection.Schema,
            RowId = mutation.RowId,
            Position = position,
            ChangedColumns = projectedColumns,
            VisibleFieldIndexes = selectedFieldIndexes
        }];
    }

    private static IReadOnlyCollection<KeyValuePair<int, string?>> FilterChangedColumns(
        IReadOnlyCollection<KeyValuePair<int, string?>> changedColumns,
        FieldMask visibleMask)
    {
        if (visibleMask.IsEmpty)
        {
            return [];
        }

        var filtered = new List<KeyValuePair<int, string?>>(changedColumns.Count);
        foreach (var (fieldIndex, value) in changedColumns)
        {
            if (visibleMask[fieldIndex])
            {
                filtered.Add(new KeyValuePair<int, string?>(fieldIndex, value));
            }
        }

        return filtered;
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

    private readonly record struct MutationImpact(bool NeedsFullRecompute, bool SortFieldTouched);
}
