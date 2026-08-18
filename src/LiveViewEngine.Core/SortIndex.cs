using LiveViewEngine.Collections;
using LiveViewEngine.Core.Data;
using System.Threading;

namespace LiveViewEngine.Core;

public sealed class SortIndex : IRowIndex
{
    private readonly RowCollection _collection;
    private readonly int _fieldIndex;
    private readonly bool _ascending;
    private readonly Comparison<int> _comparison;
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
        var fieldType = collection.Schema.GetFieldDefinition(fieldIndex).Type;
        if (fieldType != ScalarFieldType.String)
        {
            collection.AddTypedFieldRef(fieldIndex);
        }

        _comparison = fieldType switch
        {
            ScalarFieldType.Int32 => CompareInt32,
            ScalarFieldType.Int64 => CompareInt64,
            ScalarFieldType.Double => CompareDouble,
            ScalarFieldType.Decimal => CompareDecimal,
            ScalarFieldType.DateOnly => CompareDateOnly,
            ScalarFieldType.DateTime => CompareDateTime,
            ScalarFieldType.DateTimeOffset => CompareDateTimeOffset,
            _ => CompareString
        };
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

    public void TakeReverse(int startIndex, Span<int> destination) => _tree.TakeReverse(startIndex, destination);

    internal NodeArrayTree<RowComparer>.TreeCursor GetCursor(int startIndex) => _tree.GetCursor(startIndex);

    internal NodeArrayTree<RowComparer>.ReverseTreeCursor GetReverseCursor(int startIndex) => _tree.GetReverseCursor(startIndex);

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

    private int? GetInt32WithOverride(int rowIndex) =>
        rowIndex == _overrideRowIndex
            ? (ScalarValueConverter.TryConvertInt32(_overrideValue, out var v) ? v : null)
            : _collection.GetInt32(rowIndex, _fieldIndex);

    private long? GetInt64WithOverride(int rowIndex) =>
        rowIndex == _overrideRowIndex
            ? (ScalarValueConverter.TryConvertInt64(_overrideValue, out var v) ? v : null)
            : _collection.GetInt64(rowIndex, _fieldIndex);

    private double? GetDoubleWithOverride(int rowIndex) =>
        rowIndex == _overrideRowIndex
            ? (ScalarValueConverter.TryConvertDouble(_overrideValue, out var v) ? v : null)
            : _collection.GetDouble(rowIndex, _fieldIndex);

    private decimal? GetDecimalWithOverride(int rowIndex) =>
        rowIndex == _overrideRowIndex
            ? (ScalarValueConverter.TryConvertDecimal(_overrideValue, out var v) ? v : null)
            : _collection.GetDecimal(rowIndex, _fieldIndex);

    private DateOnly? GetDateOnlyWithOverride(int rowIndex) =>
        rowIndex == _overrideRowIndex
            ? (ScalarValueConverter.TryConvertDateOnly(_overrideValue, out var v) ? v : null)
            : _collection.GetDateOnly(rowIndex, _fieldIndex);

    private DateTime? GetDateTimeWithOverride(int rowIndex) =>
        rowIndex == _overrideRowIndex
            ? (ScalarValueConverter.TryConvertDateTime(_overrideValue, out var v) ? v : null)
            : _collection.GetDateTime(rowIndex, _fieldIndex);

    private DateTimeOffset? GetDateTimeOffsetWithOverride(int rowIndex) =>
        rowIndex == _overrideRowIndex
            ? (ScalarValueConverter.TryConvertDateTimeOffset(_overrideValue, out var v) ? v : null)
            : _collection.GetDateTimeOffset(rowIndex, _fieldIndex);

    private int CompareInt32(int leftRowIndex, int rightRowIndex)
    {
        var left = GetInt32WithOverride(leftRowIndex);
        var right = GetInt32WithOverride(rightRowIndex);
        if (left.HasValue && right.HasValue)
        {
            return left.Value.CompareTo(right.Value);
        }

        return CompareStringFallback(GetRawWithOverride(leftRowIndex), GetRawWithOverride(rightRowIndex));
    }

    private int CompareInt64(int leftRowIndex, int rightRowIndex)
    {
        var left = GetInt64WithOverride(leftRowIndex);
        var right = GetInt64WithOverride(rightRowIndex);
        if (left.HasValue && right.HasValue)
        {
            return left.Value.CompareTo(right.Value);
        }

        return CompareStringFallback(GetRawWithOverride(leftRowIndex), GetRawWithOverride(rightRowIndex));
    }

    private int CompareDouble(int leftRowIndex, int rightRowIndex)
    {
        var left = GetDoubleWithOverride(leftRowIndex);
        var right = GetDoubleWithOverride(rightRowIndex);
        if (left.HasValue && right.HasValue)
        {
            return left.Value.CompareTo(right.Value);
        }

        return CompareStringFallback(GetRawWithOverride(leftRowIndex), GetRawWithOverride(rightRowIndex));
    }

    private int CompareDecimal(int leftRowIndex, int rightRowIndex)
    {
        var left = GetDecimalWithOverride(leftRowIndex);
        var right = GetDecimalWithOverride(rightRowIndex);
        if (left.HasValue && right.HasValue)
        {
            return left.Value.CompareTo(right.Value);
        }

        return CompareStringFallback(GetRawWithOverride(leftRowIndex), GetRawWithOverride(rightRowIndex));
    }

    private int CompareDateOnly(int leftRowIndex, int rightRowIndex)
    {
        var left = GetDateOnlyWithOverride(leftRowIndex);
        var right = GetDateOnlyWithOverride(rightRowIndex);
        if (left.HasValue && right.HasValue)
        {
            return left.Value.CompareTo(right.Value);
        }

        return CompareStringFallback(GetRawWithOverride(leftRowIndex), GetRawWithOverride(rightRowIndex));
    }

    private int CompareDateTime(int leftRowIndex, int rightRowIndex)
    {
        var left = GetDateTimeWithOverride(leftRowIndex);
        var right = GetDateTimeWithOverride(rightRowIndex);
        if (left.HasValue && right.HasValue)
        {
            return left.Value.CompareTo(right.Value);
        }

        return CompareStringFallback(GetRawWithOverride(leftRowIndex), GetRawWithOverride(rightRowIndex));
    }

    private int CompareDateTimeOffset(int leftRowIndex, int rightRowIndex)
    {
        var left = GetDateTimeOffsetWithOverride(leftRowIndex);
        var right = GetDateTimeOffsetWithOverride(rightRowIndex);
        if (left.HasValue && right.HasValue)
        {
            return left.Value.CompareTo(right.Value);
        }

        return CompareStringFallback(GetRawWithOverride(leftRowIndex), GetRawWithOverride(rightRowIndex));
    }

    private string? GetRawWithOverride(int rowIndex) =>
        rowIndex == _overrideRowIndex ? _overrideValue : _collection.GetValue(rowIndex, _fieldIndex);

    private int CompareString(int leftRowIndex, int rightRowIndex) =>
        CompareStringFallback(GetRawWithOverride(leftRowIndex), GetRawWithOverride(rightRowIndex));

    private static int CompareStringFallback(string? left, string? right)
    {
        if (left is null && right is null)
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        return string.Compare(left, right, StringComparison.Ordinal);
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
            int cmp = _owner._comparison(a, b);
            if (!_ascending)
            {
                cmp = -cmp;
            }

            return cmp != 0 ? cmp : a.CompareTo(b);
        }
    }
}
