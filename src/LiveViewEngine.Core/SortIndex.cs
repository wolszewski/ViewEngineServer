using LiveViewEngine.Collections;
using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core;

public sealed class SortIndex
{
    private readonly RowCollection _collection;
    private readonly int _fieldIndex;
    private readonly bool _ascending;
    private readonly NodeArrayTree<RowComparer> _tree;
    private bool _hasPending;
    private bool _pendingWasExisting;
    private string? _pendingOldValue;

    internal int _overrideRowIndex = -1;
    internal string? _overrideValue;

    public SortIndex(RowCollection collection, int fieldIndex, bool ascending = true)
    {
        _collection = collection;
        _fieldIndex = fieldIndex;
        _ascending = ascending;
        _tree = new NodeArrayTree<RowComparer>(new RowComparer(this, ascending));

        foreach (var kv in collection.GetAllLiveIndexes())
        {
            _tree.Insert(kv.Value);
        }
    }

    public int Count => _tree.Count;
    public int FieldIndex => _fieldIndex;

    internal NodeArrayTree<RowComparer>.TreeCursor GetCursor(int startIndex) => _tree.GetCursor(startIndex);

    internal RowComparer GetComparer() => new(this, _ascending);

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

    internal void CaptureOldValue(int rowIndex)
    {
        int pos = _tree.IndexOf(rowIndex);
        _pendingWasExisting = pos >= 0;
        _pendingOldValue = _pendingWasExisting
            ? _collection.GetValue(rowIndex, _fieldIndex)
            : null;
        _hasPending = true;
    }

    internal void ResetPending()
    {
        _hasPending = false;
        _pendingWasExisting = false;
        _pendingOldValue = null;
    }

    public void OnUpsert(int index)
    {
        if (_hasPending && _pendingWasExisting)
        {
            try
            {
                _overrideRowIndex = index;
                _overrideValue = _pendingOldValue;
                _tree.Delete(index);
            }
            finally
            {
                _overrideRowIndex = -1;
                _overrideValue = null;
            }
        }

        _tree.Insert(index);
        ResetPending();
    }

    public void OnDelete(int index)
    {
        if (!_hasPending || !_pendingWasExisting)
        {
            ResetPending();
            return;
        }

        try
        {
            _overrideRowIndex = index;
            _overrideValue = _pendingOldValue;
            _tree.Delete(index);
        }
        finally
        {
            _overrideRowIndex = -1;
            _overrideValue = null;
            ResetPending();
        }
    }

    public int IndexOf(int index) => _tree.IndexOf(index);

    public int GetByIndex(int index) => _tree.GetByIndex(index);

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

    internal readonly struct RowComparer : IComparer<int>
    {
        private readonly SortIndex _owner;
        private readonly bool _ascending;

        internal RowComparer(SortIndex owner, bool ascending)
        {
            _owner = owner;
            _ascending = ascending;
        }

        public int Compare(int a, int b)
        {
            string? va = a == _owner._overrideRowIndex
                ? _owner._overrideValue
                : _owner._collection.GetValue(a, _owner._fieldIndex);
            string? vb = b == _owner._overrideRowIndex
                ? _owner._overrideValue
                : _owner._collection.GetValue(b, _owner._fieldIndex);
            int cmp;
            if (va is null)
            {
                if (vb is null)
                {
                    return 0;
                }

                return _ascending ? -1 : 1;
            }

            if (vb is null)
            {
                return _ascending ? 1 : -1;
            }

            cmp = string.Compare(va, vb, StringComparison.Ordinal);
            if (!_ascending)
            {
                cmp = -cmp;
            }

            return cmp != 0 ? cmp : a.CompareTo(b);
        }
    }
}
