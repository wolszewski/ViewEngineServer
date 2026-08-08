using System.Buffers;
using System.Collections.Concurrent;
using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core.Views;

public sealed class SharedView
{
    public ViewKey Key { get; }

    private readonly int _sortFieldIndex;
    private readonly int[] _filterFieldIndexes;
    private readonly FieldMask _filterFields;
    private readonly SortIndex _sortIndex;

    private readonly ConcurrentDictionary<string, bool> _subscribers = new();

    public SharedView(ViewKey key, RowCollection collection)
    {
        Key = key;

        _sortFieldIndex = key.SortColumn is not null
            ? collection.Schema.GetFieldIndex(key.SortColumn)
            : -1;
        if (_sortFieldIndex < 0)
        {
            _sortFieldIndex = collection.Schema.PrimaryKey.FieldIndex;
        }

        _filterFieldIndexes = key.Filters.Count > 0
            ? key.Filters.Select(f => collection.Schema.GetFieldIndex(f.FieldName)).ToArray()
            : [];
        _filterFields = FieldMask.From(_filterFieldIndexes.AsSpan());

        _sortIndex = new SortIndex(collection, _sortFieldIndex, key.SortAscending);
    }

    public int SortFieldIndex => _sortFieldIndex;

    public IEnumerable<string> Subscribers => _subscribers.Keys;
    public bool IsEmpty => _subscribers.IsEmpty;

    public void AddSubscriber(string connectionId) => _subscribers[connectionId] = true;

    public bool RemoveSubscriber(string connectionId) =>
        _subscribers.TryRemove(connectionId, out _);

    public int[] GetPageIndexes(int startIndex, int? pageSize)
    {
        if (startIndex < 0) { startIndex = 0; }
        if (pageSize is <= 0) { return []; }

        if (_filterFieldIndexes.Length == 0)
        {
            int total = _sortIndex.Count;
            if (startIndex >= total) { return []; }
            int take = pageSize.HasValue ? Math.Min(pageSize.Value, total - startIndex) : total - startIndex;
            var result = new int[take];
            var cursor = _sortIndex.GetCursor(startIndex);
            for (int i = 0; i < take; i++)
            {
                cursor.MoveNext();
                result[i] = cursor.Current;
            }
            return result;
        }

        int capacity = pageSize ?? _sortIndex.Count;
        var rented = ArrayPool<int>.Shared.Rent(capacity);
        try
        {
            int skipped = 0, count = 0;
            foreach (var rowIndex in _sortIndex.EnumerateFiltered(Key.Filters, _filterFieldIndexes))
            {
                if (skipped < startIndex) { skipped++; continue; }
                rented[count++] = rowIndex;
                if (pageSize.HasValue && count >= pageSize.Value) { break; }
            }
            var result = new int[count];
            rented.AsSpan(0, count).CopyTo(result);
            return result;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    public int GetTotalCount() =>
        _filterFieldIndexes.Length == 0
            ? _sortIndex.Count
            : _sortIndex.CountFiltered(Key.Filters, _filterFieldIndexes);

    public void NotifyUpsert(int index, string? newSortValue) =>
        _sortIndex.OnUpsert(index, newSortValue);

    public void NotifyDelete(int index) =>
        _sortIndex.OnDelete(index);

    public (bool SortFieldChanged, bool FilterFieldChanged) TouchedFields(in FieldMask changedMask)
    {
        bool sortTouched = changedMask[_sortFieldIndex];
        bool filterTouched = _filterFields.Intersects(changedMask);
        return (sortTouched, filterTouched);
    }
}
