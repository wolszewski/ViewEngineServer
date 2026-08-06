using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core;

//TODO: rewrite to BTree or RBTree
public sealed class SortIndex
{
    private readonly RowCollection _collection;
    private readonly int _fieldIndex;
    private readonly bool _ascending;
    private readonly List<int> _sortedIndexes;
    private readonly Dictionary<int, string?> _indexValues;

    public SortIndex(RowCollection collection, int fieldIndex, bool ascending = true)
    {
        _collection = collection;
        _fieldIndex = fieldIndex;
        _ascending = ascending;

        var allRows = collection.GetAllLiveIndexes();
        _sortedIndexes = new List<int>(allRows.Count);
        _indexValues = new Dictionary<int, string?>(allRows.Count);

        foreach (var liveRow in allRows)
        {
            var index = liveRow.Value;
            var val = collection.GetValue(index, fieldIndex);
            _indexValues[index] = val;
            _sortedIndexes.Add(index);
        }
        _sortedIndexes.Sort((a, b) => CompareByIndex(a, b));
    }


    public void OnUpsert(int index, string? newSortValue)
    {
        if (_indexValues.ContainsKey(index))
        {
            RemoveIndex(index);
        }

        _indexValues[index] = newSortValue;
        InsertIndex(index);
    }

    public void OnDelete(int index)
    {
        if (!_indexValues.ContainsKey(index))
        {
            return;
        }

        RemoveIndex(index);
        _indexValues.Remove(index);
    }


    public int[] GetPageIndexes(int startIndex, int pageSize,
        IReadOnlyList<FilterSpec>? filters = null,
        int[]? filterFieldIndexes = null)
    {
        bool filtered = filters is { Count: > 0 };
        var result = new List<int>(pageSize);
        int skipped = 0;

        foreach (var index in _sortedIndexes)
        {
            if (filtered && !PassesFilters(index, filters!, filterFieldIndexes!))
            {
                continue;
            }

            if (skipped < startIndex) { skipped++; continue; }
            result.Add(index);
            if (result.Count >= pageSize)
            {
                break;
            }
        }
        return [.. result];
    }

    public int GetCount(IReadOnlyList<FilterSpec>? filters = null, int[]? filterFieldIndexes = null)
    {
        if (filters is not { Count: > 0 })
        {
            return _sortedIndexes.Count;
        }

        int count = 0;
        foreach (var index in _sortedIndexes)
        {
            if (PassesFilters(index, filters, filterFieldIndexes!))
            {
                count++;
            }
        }

        return count;
    }


    private void InsertIndex(int index)
    {
        int lo = 0, hi = _sortedIndexes.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (CompareByIndex(_sortedIndexes[mid], index) < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        _sortedIndexes.Insert(lo, index);
    }

    private void RemoveIndex(int index)
    {
        var val = _indexValues[index];
        int lo = LowerBound(val), hi = UpperBound(val);
        for (int i = lo; i < hi; i++)
        {
            if (_sortedIndexes[i] == index)
            {
                _sortedIndexes.RemoveAt(i);
                return;
            }
        }
    }

    private int LowerBound(string? value)
    {
        int lo = 0, hi = _sortedIndexes.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (CompareValues(_indexValues[_sortedIndexes[mid]], value) < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    private int UpperBound(string? value)
    {
        int lo = 0, hi = _sortedIndexes.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (CompareValues(_indexValues[_sortedIndexes[mid]], value) <= 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    private int CompareByIndex(int a, int b)
    {
        var valueCompare = CompareValues(_indexValues[a], _indexValues[b]);
        if (valueCompare != 0)
        {
            return valueCompare;
        }

        return a.CompareTo(b);
    }

    private int CompareValues(string? a, string? b)
    {
        if (a is null && b is null)
        {
            return 0;
        }

        if (a is null)
        {
            return _ascending ? -1 : 1;
        }

        if (b is null)
        {
            return _ascending ? 1 : -1;
        }

        int cmp = string.Compare(a, b, StringComparison.Ordinal);
        return _ascending ? cmp : -cmp;
    }

    private bool PassesFilters(int index, IReadOnlyList<FilterSpec> filters, int[] fieldIndexes)
    {
        for (int i = 0; i < filters.Count; i++)
        {
            int fi = fieldIndexes[i];
            if (fi < 0)
            {
                continue;
            }

            var val = _collection.GetValue(index, fi);
            if (!FilterEvaluator.Matches(val, filters[i]))
            {
                return false;
            }
        }
        return true;
    }
}
