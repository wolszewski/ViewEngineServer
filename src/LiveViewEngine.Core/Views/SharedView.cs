using System.Collections.Concurrent;
using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core.Views;

public sealed class SharedView
{
    public ViewKey Key { get; }

    private readonly RowCollection _collection;
    private readonly int _sortFieldIndex;
    private readonly int[] _filterFieldIndexes;
    private readonly HashSet<int> _filterFieldSet;
    private readonly SortIndex _sortIndex;

    private readonly ConcurrentDictionary<string, bool> _subscribers = new();

    public SharedView(ViewKey key, RowCollection collection)
    {
        Key = key;
        _collection = collection;

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
        _filterFieldSet = [.._filterFieldIndexes];

        _sortIndex = new SortIndex(collection, _sortFieldIndex, key.SortAscending);
    }

    public int SortFieldIndex => _sortFieldIndex;

    public IEnumerable<string> Subscribers => _subscribers.Keys;
    public bool IsEmpty => _subscribers.IsEmpty;

    public void AddSubscriber(string connectionId) => _subscribers[connectionId] = true;

    public bool RemoveSubscriber(string connectionId) =>
        _subscribers.TryRemove(connectionId, out _);

    public int[] GetPageIndexes(int startIndex, int pageSize) =>
        _sortIndex.GetPageIndexes(startIndex, pageSize, Key.Filters, _filterFieldIndexes);

    public int GetTotalCount() =>
        _sortIndex.GetCount(Key.Filters, _filterFieldIndexes);

    public void NotifyUpsert(int index, string? newSortValue) =>
        _sortIndex.OnUpsert(index, newSortValue);

    public void NotifyDelete(int index) =>
        _sortIndex.OnDelete(index);

    public bool SortFieldTouched(IReadOnlyCollection<KeyValuePair<int, string?>>? changedColumns)
    {
        if (changedColumns is null) { return false; }
        foreach (var (col, _) in changedColumns)
        {
            if (col == _sortFieldIndex) { return true; }
        }
        return false;
    }

    public bool FilterFieldTouched(IReadOnlyCollection<KeyValuePair<int, string?>>? changedColumns)
    {
        if (changedColumns is null || _filterFieldSet.Count == 0) { return false; }
        foreach (var (col, _) in changedColumns)
        {
            if (_filterFieldSet.Contains(col)) { return true; }
        }
        return false;
    }

    public (bool SortFieldChanged, bool FilterFieldChanged) TouchedFields(
        IReadOnlyCollection<KeyValuePair<int, string?>>? changedColumns)
    {
        if (changedColumns is null) { return (false, false); }
        bool sortTouched = false;
        bool filterTouched = false;
        foreach (var (col, _) in changedColumns)
        {
            if (!sortTouched && col == _sortFieldIndex) { sortTouched = true; }
            if (!filterTouched && _filterFieldSet.Contains(col)) { filterTouched = true; }
            if (sortTouched && filterTouched) { break; }
        }
        return (sortTouched, filterTouched);
    }
}
