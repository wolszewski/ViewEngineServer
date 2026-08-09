using System.Collections.Concurrent;
using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core.Views;

public sealed class SharedView
{
    public ViewKey Key { get; }

    private readonly RowCollection _collection;
    private readonly int _sortFieldIndex;
    private readonly int[] _filterFieldIndexes;
    private readonly FieldMask _filterFields;
    private readonly SortIndex _sortIndex;
    private readonly FilteredDataIndex? _filteredIndex;
    private readonly ConcurrentDictionary<string, bool> _subscribers = new();

    public SharedView(ViewKey key, RowCollection collection, SortIndex sortIndex)
    {
        Key = key;
        _collection = collection;
        _sortIndex = sortIndex;
        _sortFieldIndex = sortIndex.FieldIndex;

        _filterFieldIndexes = key.Filters.Count > 0
            ? key.Filters.Select(f => collection.Schema.GetFieldIndex(f.FieldName)).ToArray()
            : Array.Empty<int>();
        _filterFields = FieldMask.From(_filterFieldIndexes.AsSpan());

        if (_filterFieldIndexes.Length <= 0)
        {
            return;
        }

        _filteredIndex = new FilteredDataIndex(_sortIndex.GetComparer(), _sortIndex.EnumerateFiltered(key.Filters, _filterFieldIndexes));
    }

    public int SortFieldIndex => _sortFieldIndex;
    internal SortIndex SortIndex => _sortIndex;

    public IEnumerable<string> Subscribers => _subscribers.Keys;

    public bool IsEmpty => _subscribers.IsEmpty;

    internal int FilteredCount => _filteredIndex?.Count ?? _sortIndex.Count;

    public void AddSubscriber(string connectionId) => _subscribers[connectionId] = true;

    public bool RemoveSubscriber(string connectionId) =>
        _subscribers.TryRemove(connectionId, out _);

    public int[] GetPageIndexes(int startIndex, int? pageSize)
    {
        if (startIndex < 0)
        {
            startIndex = 0;
        }

        if (pageSize is <= 0)
        {
            return [];
        }

        int total = _filteredIndex?.Count ?? _sortIndex.Count;
        if (startIndex >= total)
        {
            return [];
        }

        int take = pageSize.HasValue ? Math.Min(pageSize.Value, total - startIndex) : total - startIndex;
        var result = new int[take];

        if (_filteredIndex != null)
        {
            var cursor = _filteredIndex.GetCursor(startIndex);
            for (int i = 0; i < take; i++)
            {
                cursor.MoveNext();
                result[i] = cursor.Current;
            }

            return result;
        }

        var unfilteredCursor = _sortIndex.GetCursor(startIndex);
        for (int i = 0; i < take; i++)
        {
            unfilteredCursor.MoveNext();
            result[i] = unfilteredCursor.Current;
        }

        return result;
    }

    public int GetTotalCount() => _filteredIndex?.Count ?? _sortIndex.Count;

    internal int GetFilteredByIndex(int position) =>
        _filteredIndex != null ? _filteredIndex.GetByIndex(position) : _sortIndex.GetByIndex(position);

    internal int FilteredIndexOf(int rowIndex) =>
        _filteredIndex != null ? _filteredIndex.IndexOf(rowIndex) : _sortIndex.IndexOf(rowIndex);

    internal int PrepareUpsert(int rowIndex, bool isNew)
    {
        if (isNew)
        {
            return -1;
        }

        return _filteredIndex != null
            ? _filteredIndex.TryDelete(rowIndex)
            : _sortIndex.IndexOf(rowIndex);
    }

    internal int CompleteUpsert(int rowIndex)
    {
        if (_filteredIndex != null)
        {
            return PassesFilters(rowIndex) ? _filteredIndex.Insert(rowIndex) : -1;
        }

        return _sortIndex.IndexOf(rowIndex);
    }

    internal int PrepareDelete(int rowIndex)
    {
        return _filteredIndex != null
            ? _filteredIndex.TryDelete(rowIndex)
            : _sortIndex.IndexOf(rowIndex);
    }

    public (bool SortFieldChanged, bool FilterFieldChanged) TouchedFields(in FieldMask changedMask)
    {
        bool sortTouched = changedMask[_sortFieldIndex];
        bool filterTouched = _filterFields.Intersects(changedMask);
        return (sortTouched, filterTouched);
    }

    private bool PassesFilters(int index)
    {
        for (int i = 0; i < Key.Filters.Count; i++)
        {
            int fieldIndex = _filterFieldIndexes[i];
            if (fieldIndex < 0)
            {
                continue;
            }

            var value = _collection.GetValue(index, fieldIndex);
            if (!FilterEvaluator.Matches(value, Key.Filters[i]))
            {
                return false;
            }
        }

        return true;
    }
}
