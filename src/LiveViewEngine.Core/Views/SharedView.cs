using System.Collections.Concurrent;
using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core.Views;

public sealed class SharedView
{
    public ViewKey Key { get; }
    private readonly bool _sortAscending;
    private readonly RowCollection _collection;
    private readonly SortIndex _sortIndex;
    private readonly FilterSet _filters;
    private readonly FilteredDataIndex? _filteredIndex;
    private readonly IRowIndex _activeIndex;
    private readonly ConcurrentDictionary<string, bool> _subscribers = new();

    public SharedView(ViewKey key, RowCollection collection, SortIndex sortIndex)
    {
        Key = key;
        _sortAscending = key.SortAscending;
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
        if (_sortAscending)
        {
            _activeIndex.Take(startIndex, result);
            return result;
        }

        _activeIndex.TakeReverse(total - 1 - startIndex, result);

        return result;
    }

    public int GetTotalCount() => _activeIndex.Count;

    internal int GetFilteredByIndex(int position)
    {
        return _activeIndex.GetByIndex(ToIndexPosition(position, _activeIndex.Count));
    }

    internal int FilteredIndexOf(int rowIndex)
    {
        return ToViewPosition(_activeIndex.IndexOf(rowIndex), _activeIndex.Count);
    }

    internal int PrepareUpsert(int rowIndex, bool isNew)
    {
        if (isNew)
        {
            return -1;
        }

        int originalCount = _activeIndex.Count;
        int basePosition = _filteredIndex != null
            ? _sortIndex.WithPendingOldValue(rowIndex, () => _filteredIndex.TryDelete(rowIndex))
            : _sortIndex.IndexOfWithPendingOldValue(rowIndex);
        return ToViewPosition(basePosition, originalCount);
    }

    internal int CompleteUpsert(int rowIndex)
    {
        if (_filteredIndex != null)
        {
            return PassesFilters(rowIndex)
                ? ToViewPosition(_filteredIndex.Insert(rowIndex), _activeIndex.Count)
                : -1;
        }

        return ToViewPosition(_sortIndex.IndexOf(rowIndex), _activeIndex.Count);
    }

    internal int PrepareDelete(int rowIndex)
    {
        int originalCount = _activeIndex.Count;
        int basePosition = _filteredIndex != null
            ? _sortIndex.WithPendingOldValue(rowIndex, () => _filteredIndex.TryDelete(rowIndex))
            : _sortIndex.IndexOfWithPendingOldValue(rowIndex);
        return ToViewPosition(basePosition, originalCount);
    }

    public (bool SortFieldChanged, bool FilterFieldChanged) TouchedFields(in FieldMask changedMask)
    {
        bool sortTouched = changedMask[_sortIndex.FieldIndex];
        bool filterTouched = _filters.Mask.Intersects(changedMask);
        return (sortTouched, filterTouched);
    }

    private bool PassesFilters(int rowIndex) => _filters.Passes(_collection, rowIndex);

    private int ToViewPosition(int indexPosition, int count)
    {
        if (indexPosition < 0)
        {
            return -1;
        }

        return _sortAscending ? indexPosition : count - 1 - indexPosition;
    }

    private int ToIndexPosition(int viewPosition, int count)
    {
        if (viewPosition < 0 || viewPosition >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(viewPosition));
        }

        return _sortAscending ? viewPosition : count - 1 - viewPosition;
    }
}