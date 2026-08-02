using System.Collections.Concurrent;

namespace LiveViewEngine.Core.Views;

public sealed class SharedView
{
    public ViewKey Key { get; }

    private readonly RowCollection _collection;
    private readonly int _sortFieldIndex;
    private readonly int[] _filterFieldIndexes;

    public SortIndex SortIndex { get; }

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
            _sortFieldIndex = collection.Schema.PrimaryKeyIndex;
        }

        _filterFieldIndexes = key.Filters.Count > 0
            ? key.Filters.Select(f => collection.Schema.GetFieldIndex(f.FieldName)).ToArray()
            : [];

        SortIndex = new SortIndex(collection, _sortFieldIndex, key.SortAscending);
    }

    public int SortFieldIndex => _sortFieldIndex;

    public IEnumerable<string> Subscribers => _subscribers.Keys;
    public bool IsEmpty => _subscribers.IsEmpty;

    public void AddSubscriber(string connectionId) => _subscribers[connectionId] = true;

    public bool RemoveSubscriber(string connectionId) =>
        _subscribers.TryRemove(connectionId, out _);


    public int[] GetPageIndexes(int startIndex, int pageSize) =>
        SortIndex.GetPageIndexes(startIndex, pageSize, Key.Filters, _filterFieldIndexes);

    public int GetTotalCount() =>
        SortIndex.GetCount(Key.Filters, _filterFieldIndexes);


    public void NotifyUpsert(int index, string? newSortValue) =>
        SortIndex.OnUpsert(index, newSortValue);

    public void NotifyDelete(int index) =>
        SortIndex.OnDelete(index);
}
