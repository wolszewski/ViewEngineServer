using System.Collections.Concurrent;
using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core.Views;

public sealed class SharedView
{
    public ViewKey Key { get; }
    private readonly RowCollection _collection;
    private readonly SortIndex _sortIndex;
    private readonly FilterSet _filters;
    private readonly FilteredDataIndex? _filteredIndex;
    private readonly IRowIndex _activeIndex;
    private readonly ConcurrentDictionary<string, bool> _subscribers = new();

    public SharedView(ViewKey key, RowCollection collection, SortIndex sortIndex)
    {
        Key = key;
        _collection = collection;
        _sortIndex = sortIndex;

        _filters = FilterSet.Create(key.Filters, collection.Schema);
        if (_filters.HasFilters)
        {
            _filteredIndex = new FilteredDataIndex(_sortIndex.GetComparer(), _sortIndex.EnumerateFiltered(_filters));
        }
        _activeIndex = (IRowIndex?)_filteredIndex ?? sortIndex;
    }

    internal SortIndex SortIndex => _sortIndex;

    public IEnumerable<string> Subscribers => _subscribers.Keys;

    public bool IsEmpty => _subscribers.IsEmpty;

    internal int FilteredCount => _activeIndex.Count;

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

        int total = _activeIndex.Count;
        if (startIndex >= total)
        {
            return [];
        }

        int take = pageSize.HasValue ? Math.Min(pageSize.Value, total - startIndex) : total - startIndex;
        var result = new int[take];
        _activeIndex.Take(startIndex, result);
        return result;
    }

    public int GetTotalCount() => _activeIndex.Count;

    internal int GetFilteredByIndex(int position) => _activeIndex.GetByIndex(position);

    internal int FilteredIndexOf(int rowIndex) => _activeIndex.IndexOf(rowIndex);

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
        bool sortTouched = changedMask[_sortIndex.FieldIndex];
        bool filterTouched = _filters.Mask.Intersects(changedMask);
        return (sortTouched, filterTouched);
    }

    private bool PassesFilters(int rowIndex) => _filters.Passes(_collection, rowIndex);
}