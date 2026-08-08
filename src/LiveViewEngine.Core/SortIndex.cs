using System.Collections.Generic;
using LiveViewEngine.Collections;
using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core;

public sealed class SortIndex
{
    private readonly RowCollection _collection;
    private readonly int _fieldIndex;
    private readonly bool _ascending;
    private readonly NodeArrayTree<RowComparer> _tree;
    private readonly Dictionary<int, string?> _indexValues;

    public SortIndex(RowCollection collection, int fieldIndex, bool ascending = true)
    {
        _collection = collection;
        _fieldIndex = fieldIndex;
        _ascending = ascending;

        var allRows = collection.GetAllLiveIndexes();
        _indexValues = new Dictionary<int, string?>(allRows.Count);
        _tree = new NodeArrayTree<RowComparer>(new RowComparer(_indexValues, ascending));

        foreach (var liveRow in allRows)
        {
            var index = liveRow.Value;
            _indexValues[index] = collection.GetValue(index, fieldIndex);
        }

        // Insert in sorted order to minimise tree rotations during initial build.
        var sorted = _indexValues.Keys.ToList();
        sorted.Sort(new RowComparer(_indexValues, ascending));
        foreach (var index in sorted)
        {
            _tree.Insert(index);
        }
    }

    public int Count => _tree.Count;

    // Valid only within the current synchronous call — do not store the span.
    // Kept for callers that need a zero-allocation sorted snapshot (unfiltered fast path).
    internal NodeArrayTree<RowComparer>.TreeCursor GetCursor(int startIndex) => _tree.GetCursor(startIndex);

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

    internal readonly struct RowComparer : IComparer<int>
    {
        private readonly Dictionary<int, string?> _values;
        private readonly bool _ascending;

        internal RowComparer(Dictionary<int, string?> values, bool ascending)
        {
            _values = values;
            _ascending = ascending;
        }

        public int Compare(int a, int b)
        {
            string? va = _values[a];
            string? vb = _values[b];
            int cmp;
            if (va is null)
            {
                if (vb is null) { return 0; }
                return _ascending ? -1 : 1;
            }
            if (vb is null) { return _ascending ? 1 : -1; }
            cmp = string.Compare(va, vb, StringComparison.Ordinal);
            if (!_ascending) { cmp = -cmp; }
            return cmp != 0 ? cmp : a.CompareTo(b);
        }
    }
}



