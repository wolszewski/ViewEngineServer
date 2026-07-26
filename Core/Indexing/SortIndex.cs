using ViewEngineServer.Core.Indexing;
using ViewEngineServer.Core.Storage;

namespace ViewEngineServer.Core.Indexing;

/// <summary>
/// Maintains a sorted list of row handles for one (collection, field, direction) triple.
///
/// Sorting is kept up-to-date incrementally: O(log N) binary search for the insertion
/// point plus O(N) list shift. A future optimisation can replace the List with a
/// skip-list or B-tree without changing the public surface.
///
/// Thread-safety: all public methods are guarded by an internal lock so callers
/// do not need to synchronise externally.
/// </summary>
public sealed class SortIndex
{
    private readonly ColumnarCollection _collection;
    private readonly int _fieldIndex;
    private readonly bool _ascending;

    // Handles in sorted order. Only live handles are stored here.
    private readonly List<int> _sortedHandles;

    // Per-handle copy of the sort-field value. Used for binary-search position
    // lookups without reading from the collection (which may have been mutated
    // before OnUpsert is called).
    private readonly Dictionary<int, object?> _handleValues;

    private readonly object _lock = new();

    public SortIndex(ColumnarCollection collection, int fieldIndex, bool ascending = true)
    {
        _collection = collection;
        _fieldIndex = fieldIndex;
        _ascending = ascending;

        var allRows = collection.GetAllLiveHandles();
        _sortedHandles = new List<int>(allRows.Count);
        _handleValues = new Dictionary<int, object?>(allRows.Count);

        foreach (var (handle, _) in allRows)
        {
            var val = collection.GetValue(handle, fieldIndex);
            _handleValues[handle] = val;
            _sortedHandles.Add(handle);
        }
        _sortedHandles.Sort((a, b) => CompareByHandle(a, b));
    }

    // -----------------------------------------------------------------------
    // Mutation notifications — called by ViewEngine after storage is updated
    // -----------------------------------------------------------------------

    /// <summary>
    /// Notify the index that a row was inserted or updated.
    /// The collection must have already been written with the new value.
    /// </summary>
    public void OnUpsert(int handle, object? newSortValue)
    {
        lock (_lock)
        {
            if (_handleValues.ContainsKey(handle))
                RemoveHandle(handle); // repositions correctly using old tracked value

            _handleValues[handle] = newSortValue;
            InsertHandle(handle);
        }
    }

    /// <summary>Notify the index that a row was deleted.</summary>
    public void OnDelete(int handle)
    {
        lock (_lock)
        {
            if (!_handleValues.ContainsKey(handle)) return;
            RemoveHandle(handle);
            _handleValues.Remove(handle);
        }
    }

    // -----------------------------------------------------------------------
    // Queries
    // -----------------------------------------------------------------------

    /// <summary>
    /// Return handles for the requested page in sort order.
    /// When <paramref name="filters"/> is non-empty, rows not matching the
    /// filters are skipped before applying the page offset and count.
    /// </summary>
    public int[] GetPageHandles(int startIndex, int pageSize,
        IReadOnlyList<FilterSpec>? filters = null,
        int[]? filterFieldIndexes = null)
    {
        lock (_lock)
        {
            bool filtered = filters is { Count: > 0 };
            var result = new List<int>(pageSize);
            int skipped = 0;

            foreach (var handle in _sortedHandles)
            {
                if (filtered && !PassesFilters(handle, filters!, filterFieldIndexes!))
                    continue;

                if (skipped < startIndex) { skipped++; continue; }
                result.Add(handle);
                if (result.Count >= pageSize) break;
            }
            return [.. result];
        }
    }

    /// <summary>Total number of live rows visible through the given filters.</summary>
    public int GetCount(IReadOnlyList<FilterSpec>? filters = null, int[]? filterFieldIndexes = null)
    {
        lock (_lock)
        {
            if (filters is not { Count: > 0 }) return _sortedHandles.Count;

            int count = 0;
            foreach (var handle in _sortedHandles)
                if (PassesFilters(handle, filters, filterFieldIndexes!)) count++;
            return count;
        }
    }

    // -----------------------------------------------------------------------
    // Internal helpers
    // -----------------------------------------------------------------------

    private void InsertHandle(int handle)
    {
        int lo = 0, hi = _sortedHandles.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (CompareByHandle(_sortedHandles[mid], handle) < 0) lo = mid + 1;
            else hi = mid;
        }
        _sortedHandles.Insert(lo, handle);
    }

    private void RemoveHandle(int handle)
    {
        var val = _handleValues[handle];
        int lo = LowerBound(val), hi = UpperBound(val);
        for (int i = lo; i < hi; i++)
        {
            if (_sortedHandles[i] == handle)
            {
                _sortedHandles.RemoveAt(i);
                return;
            }
        }
    }

    private int LowerBound(object? value)
    {
        int lo = 0, hi = _sortedHandles.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (CompareValues(_handleValues[_sortedHandles[mid]], value) < 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private int UpperBound(object? value)
    {
        int lo = 0, hi = _sortedHandles.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (CompareValues(_handleValues[_sortedHandles[mid]], value) <= 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private int CompareByHandle(int a, int b) =>
        CompareValues(_handleValues[a], _handleValues[b]);

    private int CompareValues(object? a, object? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return _ascending ? -1 : 1;
        if (b is null) return _ascending ? 1 : -1;

        int cmp;
        try
        {
            cmp = a is IComparable ca
                ? ca.CompareTo(Convert.ChangeType(b, a.GetType()))
                : string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
        }
        catch
        {
            cmp = string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
        }

        return _ascending ? cmp : -cmp;
    }

    private bool PassesFilters(int handle, IReadOnlyList<FilterSpec> filters, int[] fieldIndexes)
    {
        for (int i = 0; i < filters.Count; i++)
        {
            int fi = fieldIndexes[i];
            if (fi < 0) continue;
            var val = _collection.GetValue(handle, fi);
            if (!FilterEvaluator.Matches(val, filters[i])) return false;
        }
        return true;
    }
}
