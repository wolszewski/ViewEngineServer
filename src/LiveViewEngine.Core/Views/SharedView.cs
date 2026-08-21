using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core.Views;

public sealed class SharedView : IDisposable
{
    public ViewKey Key { get; }
    private readonly bool _sortAscending;
    private readonly RowCollection _collection;
    private readonly SortIndex _sortIndex;
    private readonly FilterSet _filters;
    private readonly FilteredDataIndex? _filteredIndex;
    private readonly IRowIndex _activeIndex;
    private readonly HashSet<SubscriptionKey> _subscribers = new();

    public SharedView(ViewKey key, RowCollection collection, SortIndex sortIndex, LiveViewEngineOptions? options = null)
    {
        Key = key;
        _sortAscending = key.SortAscending;
        _collection = collection;
        _sortIndex = sortIndex;

        var lifetime = options?.TypedColumnKeepAlive ?? TypedColumnKeepAlive.WhenReferencedByIndexes;
        _filters = FilterSet.Create(key.Filters, collection.Schema, collection, lifetime);
        if (_filters.HasFilters)
        {
            _filteredIndex = new FilteredDataIndex(_sortIndex.GetComparer(), _sortIndex.EnumerateFiltered(_filters));
        }
        _activeIndex = (IRowIndex?)_filteredIndex ?? sortIndex;
    }

    public void Dispose() => _filters.Dispose();

    internal SortIndex SortIndex => _sortIndex;

    public IEnumerable<SubscriptionKey> Subscribers => _subscribers;

    public bool IsEmpty => _subscribers.Count == 0;

    internal int FilteredCount => _activeIndex.Count;

    public void AddSubscriber(SubscriptionKey subscriptionKey) => _subscribers.Add(subscriptionKey);

    public bool RemoveSubscriber(SubscriptionKey subscriptionKey) => _subscribers.Remove(subscriptionKey);

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

    public IEnumerable<int> EnumeratePageIndexes(int startIndex, int? pageSize)
    {
        if (startIndex < 0)
        {
            startIndex = 0;
        }

        if (pageSize is <= 0)
        {
            yield break;
        }

        int total = _activeIndex.Count;
        if (startIndex >= total)
        {
            yield break;
        }

        int take = pageSize.HasValue ? Math.Min(pageSize.Value, total - startIndex) : total - startIndex;
        for (int i = 0; i < take; i++)
        {
            yield return GetFilteredByIndex(startIndex + i);
        }
    }

    public int GetTotalCount() => _activeIndex.Count;

    internal int GetFilteredByIndex(int position)
    {
        return _activeIndex.GetByIndex(ToIndexPosition(position, _activeIndex.Count));
    }

    internal string GetRowIdAtPosition(int position)
    {
        int rowIndex = GetFilteredByIndex(position);
        return _collection.GetValue(rowIndex, CollectionSchema.PrimaryKeyIndex)
            ?? throw new InvalidOperationException("Rows in a view must have a primary key.");
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