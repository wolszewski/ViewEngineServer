using LiveViewEngine.Collections;
using LiveViewEngine.Core.Data;
using System.Threading;

namespace LiveViewEngine.Core;

public sealed class SortIndex : IRowIndex
{
    private readonly RowCollection _collection;
    private readonly int _fieldIndex;
    private readonly bool _ascending;
    private readonly NodeArrayTree<RowComparer> _tree;
    private bool _hasPending;
    private bool _pendingWasExisting;
    private int _pendingRowIndex = -1;
    private string? _pendingOldValue;
    private int _overrideRowIndex = -1;
    private string? _overrideValue;
    private volatile int _subscriberCount;
    private long _lastUsedTicks = DateTime.UtcNow.Ticks;

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

    public int SubscriberCount => _subscriberCount;
    public DateTime LastUsedUtc => new DateTime(Interlocked.Read(ref _lastUsedTicks), DateTimeKind.Utc);

    internal void IncrementSubscribers()
    {
        Interlocked.Increment(ref _subscriberCount);
        Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);
    }

    internal void DecrementSubscribers() => Interlocked.Decrement(ref _subscriberCount);

    public void Take(int startIndex, Span<int> destination) => _tree.Take(startIndex, destination);

    internal NodeArrayTree<RowComparer>.TreeCursor GetCursor(int startIndex) => _tree.GetCursor(startIndex);

    internal RowComparer GetComparer() => new(this, _ascending);

    internal IEnumerable<int> EnumerateFiltered(FilterSet filters)
    {
        var cursor = _tree.GetCursor(0);
        while (cursor.MoveNext())
        {
            int index = cursor.Current;
            if (filters.Passes(_collection, index))
            {
                yield return index;
            }
        }
    }

    internal void CaptureOldValue(int rowIndex)
    {
        int pos = _tree.IndexOf(rowIndex);
        _pendingWasExisting = pos >= 0;
        _pendingRowIndex = rowIndex;
        _pendingOldValue = _pendingWasExisting
            ? _collection.GetValue(rowIndex, _fieldIndex)
            : null;
        _hasPending = true;
    }

    internal void ResetPending()
    {
        _hasPending = false;
        _pendingWasExisting = false;
        _pendingRowIndex = -1;
        _pendingOldValue = null;
    }

    internal int IndexOfWithPendingOldValue(int rowIndex)
    {
        return WithPendingOldValue(rowIndex, () => _tree.IndexOf(rowIndex));
    }

    internal TResult WithPendingOldValue<TResult>(int rowIndex, Func<TResult> action)
    {
        if (!_hasPending || !_pendingWasExisting || _pendingRowIndex != rowIndex)
        {
            return action();
        }

        try
        {
            _overrideRowIndex = rowIndex;
            _overrideValue = _pendingOldValue;
            return action();
        }
        finally
        {
            _overrideRowIndex = -1;
            _overrideValue = null;
        }
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
