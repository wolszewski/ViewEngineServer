using System.Runtime.CompilerServices;
using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core.Views;

public sealed class MutationPropagator
{
    private readonly IRowProjector _rowProjector;

    public MutationPropagator(IRowProjector? rowProjector = null)
    {
        _rowProjector = rowProjector ?? SelectRowProjector.Instance;
    }

    // Reusable per-propagation buffers. CollectionRuntime serializes all mutations per collection.
    private readonly Dictionary<IPositionIndex, List<SharedView>> _groupBuffer = new(ReferenceEqualityComparer.Instance);
    private readonly List<MutationImpact> _impactBuffer = [];
    private readonly List<int> _oldPosBuffer = [];

    // Returns groups of (deltas, targets) where all targets in a group share the
    // same viewport and therefore receive the same delta payload — serialize once, fan out.
    internal List<(IReadOnlyList<ViewDelta> Deltas, List<SubscriberTarget> Targets)>? Propagate(
        RowCollection collection,
        Dictionary<ViewKey, SharedView> collectionViews,
        Dictionary<SubscriptionKey, ViewportState> viewports,
        IEnumerable<IPositionIndex> positionIndexes,
        MutationInfo mutation,
        bool isDelete)
    {
        List<(IReadOnlyList<ViewDelta> Deltas, List<SubscriberTarget> Targets)>? pending = null;
        try
        {
            GroupViewsByPositionIndex(collectionViews);
            foreach (var (positionIndex, views) in _groupBuffer)
            {
                AnalyzeImpactsAndCaptureOldPositions(views, mutation, isDelete);
                ApplyPositionIndexMutation(positionIndex, mutation, isDelete);
                CollectViewDeltaGroups(collection, views, viewports, mutation, isDelete, ref pending);
                views.Clear();
            }

            UpdateIdlePositionIndexes(positionIndexes, mutation, isDelete);

            return pending;
        }
        finally
        {
            _groupBuffer.Clear();
            _impactBuffer.Clear();
            _oldPosBuffer.Clear();
        }
    }

    private void GroupViewsByPositionIndex(Dictionary<ViewKey, SharedView> collectionViews)
    {
        foreach (var entry in collectionViews)
        {
            var positionIndex = entry.Value.PositionIndex;
            if (!_groupBuffer.TryGetValue(positionIndex, out var list))
            {
                list = [];
                _groupBuffer[positionIndex] = list;
            }

            list.Add(entry.Value);
        }
    }

    private void AnalyzeImpactsAndCaptureOldPositions(List<SharedView> views, MutationInfo mutation, bool isDelete)
    {
        _impactBuffer.Clear();
        _oldPosBuffer.Clear();

        for (int i = 0; i < views.Count; i++)
        {
            var impact = AnalyzeMutationImpact(views[i], mutation, isDelete);
            _impactBuffer.Add(impact);
            _oldPosBuffer.Add(0);

            if (!impact.NeedsFullRecompute)
            {
                continue;
            }

            _oldPosBuffer[i] = isDelete
                ? views[i].PrepareDelete(mutation.RowIndex)
                : views[i].PrepareUpsert(mutation.RowIndex, mutation.IsNew);
        }
    }

    private void ApplyPositionIndexMutation(IPositionIndex positionIndex, MutationInfo mutation, bool isDelete)
    {
        if (isDelete)
        {
            positionIndex.OnDelete(mutation.RowIndex);
            return;
        }

        if (mutation.IsNew || positionIndex.AffectsOrder(mutation.ChangedMask))
        {
            positionIndex.OnUpsert(mutation.RowIndex);
            return;
        }

        positionIndex.ResetPending();
    }

    private void UpdateIdlePositionIndexes(IEnumerable<IPositionIndex> positionIndexes, MutationInfo mutation, bool isDelete)
    {
        foreach (var positionIndex in positionIndexes)
        {
            if (_groupBuffer.ContainsKey(positionIndex))
            {
                continue;
            }

            ApplyPositionIndexMutation(positionIndex, mutation, isDelete);
        }
    }

    private void CollectViewDeltaGroups(
        RowCollection collection,
        List<SharedView> views,
        Dictionary<SubscriptionKey, ViewportState> viewports,
        MutationInfo mutation,
        bool isDelete,
        ref List<(IReadOnlyList<ViewDelta> Deltas, List<SubscriberTarget> Targets)>? pending)
    {
        for (int i = 0; i < views.Count; i++)
        {
            var view = views[i];
            if (!_impactBuffer[i].NeedsFullRecompute)
            {
                CollectFastPathGroups(collection, view, viewports, mutation, ref pending);
                continue;
            }

            int newFilteredPos = isDelete ? -1 : view.CompleteUpsert(mutation.RowIndex);
            CollectPositionGroups(
                collection,
                view,
                viewports,
                mutation,
                _oldPosBuffer[i],
                newFilteredPos,
                isDelete,
                ref pending);
        }
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
        Dictionary<SubscriptionKey, ViewportState> viewports,
        MutationInfo mutation,
        ref List<(IReadOnlyList<ViewDelta>, List<SubscriberTarget>)>? pending)
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

        Dictionary<FastPathGroupKey, List<ViewportState>>? groups = null;
        foreach (var subscriptionId in view.Subscribers)
        {
            if (!viewports.TryGetValue(subscriptionId, out var viewport))
            {
                continue;
            }

            if (!viewport.VisibleColumns.Intersects(mutation.ChangedMask))
            {
                continue;
            }

            if (!IsPositionInViewport(filteredPos, viewport.StartIndex, viewport.PageSize))
            {
                continue;
            }

            groups ??= new();
            var key = new FastPathGroupKey(viewport.StartIndex, viewport.PageSize, viewport.VisibleColumns);
            if (!groups.TryGetValue(key, out var list)) { list = []; groups[key] = list; }
            list.Add(viewport);
        }

        if (groups is null) { return; }

        foreach (var (key, groupViewports) in groups)
        {
            pending ??= [];
            var viewport = groupViewports[0];
            var projectedColumns = FilterChangedColumns(mutation.ChangedColumns, viewport.VisibleColumns);
            if (projectedColumns.Count == 0)
            {
                continue;
            }

            var targets = new List<SubscriberTarget>(groupViewports.Count);
            foreach (var v in groupViewports)
            {
                targets.Add(new SubscriberTarget(v.SubscriptionKey.ConnectionId, v.SubscriptionKey.SubscriptionId));
            }

            pending.Add(([new RowUpdateDelta
            {
                ViewId = view.Key.Id,
                Schema = collection.Schema,
                RowId = mutation.RowId,
                Position = filteredPos - key.Start,
                ChangedColumns = projectedColumns,
                VisibleFieldIndexes = viewport.SelectedFieldIndexes
            }], targets));
        }
    }

    // Full-recompute path: new row, delete, sort-field change, or filter-field change.
    // Subscribers with the same viewport and projection produce the same delta sequence — compute and share.
    private void CollectPositionGroups(
        RowCollection collection,
        SharedView view,
        Dictionary<SubscriptionKey, ViewportState> viewports,
        MutationInfo mutation,
        int oldFilteredPos,
        int newFilteredPos,
        bool isDelete,
        ref List<(IReadOnlyList<ViewDelta>, List<SubscriberTarget>)>? pending)
    {
        Dictionary<ViewportGroupKey, List<ViewportState>>? groups = null;
        foreach (var subscriptionId in view.Subscribers)
        {
            if (!viewports.TryGetValue(subscriptionId, out var viewport))
            {
                continue;
            }

            groups ??= new(ViewportGroupKey.Comparer);
            var key = new ViewportGroupKey(
                viewport.StartIndex,
                viewport.PageSize,
                viewport.VisibleColumns,
                viewport.SelectedFieldIndexes);
            if (!groups.TryGetValue(key, out var list)) { list = []; groups[key] = list; }
            list.Add(viewport);
        }

        if (groups is null) { return; }

        foreach (var (key, groupViewports) in groups)
        {
            var visibleViewport = groupViewports[0];
            var deltas = ComputePositionDeltas(
                view, collection, view.Key.Id, mutation,
                oldFilteredPos, newFilteredPos, key.Start, key.PageSize,
                visibleViewport.SelectedFieldIndexes, visibleViewport.VisibleColumns, isDelete);
            if (deltas.Count == 0) { continue; }

            pending ??= [];
            var targets = new List<SubscriberTarget>(groupViewports.Count);
            foreach (var v in groupViewports)
            {
                targets.Add(new SubscriberTarget(v.SubscriptionKey.ConnectionId, v.SubscriptionKey.SubscriptionId));
            }

            pending.Add((deltas, targets));
        }
    }

    private IReadOnlyList<ViewDelta> ComputePositionDeltas(
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
        if (pageSize is { } finitePageSize)
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
            int stablePosition = newIn ? newFilteredPos - start : -1;
            return BuildUpdateDeltaIfVisible(
                viewId,
                collection,
                mutation,
                stablePosition,
                selectedFieldIndexes,
                visibleMask,
                isDelete);
        }

        if (IsMutationOutsideViewport(oldIn, oldBefore, newIn, newBefore))
        {
            return [];
        }

        var deltas = new List<ViewDelta>(3);

        if (oldIn && newIn)
        {
            AddReplaceDelta(
                deltas,
                viewId,
                collection,
                selectedFieldIndexes,
                mutation.RowId,
                oldFilteredPos - start,
                mutation.RowIndex,
                newFilteredPos - start);
        }
        else if (oldBefore && newIn)
        {
            if (Exists(start - 1, n))
            {
                AddReplaceDelta(
                    deltas,
                    viewId,
                    collection,
                    selectedFieldIndexes,
                    view.GetRowIdAtPosition(start),
                    0,
                    mutation.RowIndex,
                    newFilteredPos - start);
            }
            else
            {
                AddInsertDelta(deltas, viewId, collection, selectedFieldIndexes, mutation.RowIndex, newFilteredPos - start);
            }
        }
        else if (oldIn && newBefore)
        {
            if (Exists(start, n))
            {
                AddReplaceDelta(
                    deltas,
                    viewId,
                    collection,
                    selectedFieldIndexes,
                    mutation.RowId,
                    oldFilteredPos - start,
                    view.GetFilteredByIndex(start),
                    0);
            }
            else
            {
                AddRemoveDelta(deltas, viewId, mutation.RowId, oldFilteredPos - start);
            }
        }
        else if (!oldIn && !oldBefore && newIn)
        {
            if (hasFinitePage && Exists(end, n))
            {
                AddReplaceDelta(
                    deltas,
                    viewId,
                    collection,
                    selectedFieldIndexes,
                    view.GetRowIdAtPosition(end),
                    bottomPosition,
                    mutation.RowIndex,
                    newFilteredPos - start);
            }
            else
            {
                AddInsertDelta(deltas, viewId, collection, selectedFieldIndexes, mutation.RowIndex, newFilteredPos - start);
            }
        }
        else if (oldIn && !newIn && !newBefore)
        {
            if (hasFinitePage && Exists(end - 1, n))
            {
                AddReplaceDelta(
                    deltas,
                    viewId,
                    collection,
                    selectedFieldIndexes,
                    mutation.RowId,
                    oldFilteredPos - start,
                    view.GetFilteredByIndex(end - 1),
                    bottomPosition);
            }
            else
            {
                AddRemoveDelta(deltas, viewId, mutation.RowId, oldFilteredPos - start);
            }
        }
        else if (!oldIn && !oldBefore && newBefore)
        {
            var canInsertAtTop = Exists(start, n);
            var canRemoveAtBottom = hasFinitePage && Exists(end, n);
            if (canInsertAtTop && canRemoveAtBottom)
            {
                AddReplaceDelta(
                    deltas,
                    viewId,
                    collection,
                    selectedFieldIndexes,
                    view.GetRowIdAtPosition(end),
                    bottomPosition,
                    view.GetFilteredByIndex(start),
                    0);
            }
            else
            {
                if (canInsertAtTop)
                {
                    AddInsertDelta(
                        deltas,
                        viewId,
                        collection,
                        selectedFieldIndexes,
                        view.GetFilteredByIndex(start),
                        0);
                }

                if (canRemoveAtBottom)
                {
                    AddRemoveDelta(deltas, viewId, view.GetRowIdAtPosition(end), bottomPosition);
                }
            }
        }
        else if (oldBefore && !newIn && !newBefore)
        {
            var canRemoveAtTop = Exists(start - 1, n);
            var canInsertAtBottom = hasFinitePage && Exists(end - 1, n);
            if (canRemoveAtTop && canInsertAtBottom)
            {
                AddReplaceDelta(
                    deltas,
                    viewId,
                    collection,
                    selectedFieldIndexes,
                    view.GetRowIdAtPosition(start),
                    0,
                    view.GetFilteredByIndex(end - 1),
                    bottomPosition);
            }
            else
            {
                if (canRemoveAtTop)
                {
                    AddRemoveDelta(deltas, viewId, view.GetRowIdAtPosition(start), 0);
                }

                if (canInsertAtBottom)
                {
                    AddInsertDelta(
                        deltas,
                        viewId,
                        collection,
                        selectedFieldIndexes,
                        view.GetFilteredByIndex(end - 1),
                        bottomPosition);
                }
            }
        }

        if (newIn)
        {
            AddUpdateDelta(
                deltas,
                viewId,
                collection,
                mutation,
                newFilteredPos - start,
                selectedFieldIndexes,
                visibleMask);
        }

        return deltas;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsMutationOutsideViewport(bool oldIn, bool oldBefore, bool newIn, bool newBefore)
    {
        return (!oldIn && !oldBefore && !newIn && !newBefore) || (oldBefore && newBefore);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPositionInViewport(int position, int start, int? pageSize)
    {
        int end = pageSize.HasValue ? start + pageSize.Value : int.MaxValue;
        return position >= start && position < end;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Exists(int pos, int count)
    {
        return pos >= 0 && pos < count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddRemoveDelta(List<ViewDelta> deltas, string viewId, string rowId, int position)
    {
        deltas.Add(new RowRemoveDelta { ViewId = viewId, RowId = rowId, Position = position });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddInsertDelta(
        List<ViewDelta> deltas,
        string viewId,
        RowCollection collection,
        int[] selectedFieldIndexes,
        int rowIndex,
        int position)
    {
        var row = _rowProjector.Project(collection.GetRowValues(rowIndex), selectedFieldIndexes);
        deltas.Add(new RowInsertDelta
        {
            ViewId = viewId,
            Position = position,
            Schema = collection.Schema,
            Row = row,
            VisibleFieldIndexes = selectedFieldIndexes
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddReplaceDelta(
        List<ViewDelta> deltas,
        string viewId,
        RowCollection collection,
        int[] selectedFieldIndexes,
        string removedRowId,
        int removePosition,
        int insertRowIndex,
        int insertPosition)
    {
        var row = _rowProjector.Project(collection.GetRowValues(insertRowIndex), selectedFieldIndexes);
        deltas.Add(new RowReplaceDelta
        {
            ViewId = viewId,
            Schema = collection.Schema,
            VisibleFieldIndexes = selectedFieldIndexes,
            RemovedRowId = removedRowId,
            RemovePosition = removePosition,
            InsertPosition = insertPosition,
            Row = row
        });
    }

    private static void AddUpdateDelta(
        List<ViewDelta> deltas,
        string viewId,
        RowCollection collection,
        MutationInfo mutation,
        int position,
        int[] selectedFieldIndexes,
        FieldMask visibleMask)
    {
        if (mutation.IsNew || mutation.ChangedColumns is not { Count: > 0 })
        {
            return;
        }

        var projected = FilterChangedColumns(mutation.ChangedColumns, visibleMask);
        if (projected.Count == 0)
        {
            return;
        }

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

        return filtered.Count == changedColumns.Count ? changedColumns : filtered;
    }

    private readonly record struct FastPathGroupKey(int Start, int? PageSize, FieldMask VisibleColumns);

    private readonly record struct ViewportGroupKey(
        int Start,
        int? PageSize,
        FieldMask VisibleColumns,
        int[] SelectedFieldIndexes)
    {
        public static IEqualityComparer<ViewportGroupKey> Comparer { get; } = new ViewportGroupKeyComparer();
    }

    private sealed class ViewportGroupKeyComparer : IEqualityComparer<ViewportGroupKey>
    {
        public bool Equals(ViewportGroupKey x, ViewportGroupKey y)
        {
            if (x.Start != y.Start || x.PageSize != y.PageSize)
            {
                return false;
            }

            var xMask = x.VisibleColumns.Key;
            var yMask = y.VisibleColumns.Key;
            if (xMask.Low != yMask.Low || xMask.High != yMask.High)
            {
                return false;
            }

            return x.SelectedFieldIndexes.AsSpan().SequenceEqual(y.SelectedFieldIndexes);
        }

        public int GetHashCode(ViewportGroupKey obj)
        {
            var mask = obj.VisibleColumns.Key;
            var hash = HashCode.Combine(obj.Start, obj.PageSize, mask.Low, mask.High, obj.SelectedFieldIndexes.Length);
            foreach (var index in obj.SelectedFieldIndexes)
            {
                hash = HashCode.Combine(hash, index);
            }

            return hash;
        }
    }

    private readonly record struct MutationImpact(bool NeedsFullRecompute, bool SortFieldTouched);
}
