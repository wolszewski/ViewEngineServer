using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core;

// TODO: List<int>.Insert and List<int>.RemoveAt are O(N) — replace _sortedIndexes with a B-tree,
//       red-black tree, or order-statistics tree to get O(log N) insert/remove.
public sealed class SortIndex
{
    private readonly RowCollection _collection;
    private readonly int _fieldIndex;
    private readonly bool _ascending;
    private readonly OrderStatisticsTree _tree;
    private readonly Dictionary<int, string?> _indexValues;

    public SortIndex(RowCollection collection, int fieldIndex, bool ascending = true)
    {
        _collection = collection;
        _fieldIndex = fieldIndex;
        _ascending = ascending;

        var allRows = collection.GetAllLiveIndexes();
        _indexValues = new Dictionary<int, string?>(allRows.Count);
        _tree = new OrderStatisticsTree(CompareByIndex);

        foreach (var liveRow in allRows)
        {
            var index = liveRow.Value;
            _indexValues[index] = collection.GetValue(index, fieldIndex);
        }

        // Insert in sorted order to minimise tree rotations during initial build.
        var sorted = _indexValues.Keys.ToList();
        sorted.Sort(CompareByIndex);
        foreach (var index in sorted)
        {
            _tree.Insert(index);
        }
    }

    public int Count => _tree.Count;

    // Valid only within the current synchronous call — do not store the span.
    // Kept for callers that need a zero-allocation sorted snapshot (unfiltered fast path).
    internal OrderStatisticsTree.TreeCursor GetCursor(int startIndex) => _tree.GetCursor(startIndex);

    public IEnumerable<int> EnumerateFiltered(IReadOnlyList<FilterSpec> filters, int[] filterFieldIndexes)
    {
        var cursor = _tree.GetCursor(0);
        while (cursor.MoveNext())
        {
            int index = cursor.Current;
            if (PassesFilters(index, filters, filterFieldIndexes))
            {
                yield return index;
            }
        }
    }

    public int CountFiltered(IReadOnlyList<FilterSpec> filters, int[] filterFieldIndexes)
    {
        int count = 0;
        var cursor = _tree.GetCursor(0);
        while (cursor.MoveNext())
        {
            if (PassesFilters(cursor.Current, filters, filterFieldIndexes))
            {
                count++;
            }
        }
        return count;
    }

    public void OnUpsert(int index, string? newSortValue)
    {
        if (_indexValues.ContainsKey(index))
        {
            _tree.Delete(index); // must delete before updating _indexValues (comparison uses old value)
        }
        _indexValues[index] = newSortValue;
        _tree.Insert(index);
    }

    public void OnDelete(int index)
    {
        if (!_indexValues.ContainsKey(index)) { return; }
        _tree.Delete(index);
        _indexValues.Remove(index);
    }

    private int CompareByIndex(int a, int b)
    {
        int valueCompare = CompareValues(_indexValues[a], _indexValues[b]);
        return valueCompare != 0 ? valueCompare : a.CompareTo(b);
    }

    private int CompareValues(string? a, string? b)
    {
        if (a is null && b is null) { return 0; }
        if (a is null) { return _ascending ? -1 : 1; }
        if (b is null) { return _ascending ? 1 : -1; }
        int cmp = string.Compare(a, b, StringComparison.Ordinal);
        return _ascending ? cmp : -cmp;
    }

    private bool PassesFilters(int index, IReadOnlyList<FilterSpec> filters, int[] fieldIndexes)
    {
        for (int i = 0; i < filters.Count; i++)
        {
            int fi = fieldIndexes[i];
            if (fi < 0) { continue; }
            var val = _collection.GetValue(index, fi);
            if (!FilterEvaluator.Matches(val, filters[i])) { return false; }
        }
        return true;
    }
}


