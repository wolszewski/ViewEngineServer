using System.Collections.Concurrent;

namespace ViewEngineServer.Core;


public sealed class ViewDefinition
{
    public required string CollectionId { get; init; }
    public string? SortColumn { get; init; }
    public bool SortAscending { get; init; } = true;
    public IReadOnlyList<FilterSpec> Filters { get; init; } = [];
}

public sealed class ViewKey : IEquatable<ViewKey>
{
    public string CollectionId { get; }
    public string? SortColumn { get; }
    public bool SortAscending { get; }
    public IReadOnlyList<FilterSpec> Filters { get; }

    public string Id { get; }

    private readonly int _hashCode;

    public ViewKey(string collectionId, string? sortColumn, bool sortAscending,
                   IReadOnlyList<FilterSpec>? filters)
    {
        CollectionId = collectionId;
        SortColumn = sortColumn;
        SortAscending = sortAscending;
        Filters = filters ?? [];

        Id = $"{collectionId}|{sortColumn}|{(sortAscending ? "asc" : "desc")}|" +
             string.Join(",", Filters.Select(f => $"{f.FieldName}:{f.Operator}:{f.Value}"));

        _hashCode = ComputeHash();
    }

    public static ViewKey From(ViewDefinition def) =>
        new(def.CollectionId, def.SortColumn, def.SortAscending, def.Filters);

    public bool Equals(ViewKey? other)
    {
        if (other is null) return false;
        if (CollectionId != other.CollectionId) return false;
        if (SortColumn != other.SortColumn) return false;
        if (SortAscending != other.SortAscending) return false;
        if (Filters.Count != other.Filters.Count) return false;
        for (int i = 0; i < Filters.Count; i++)
        {
            var f = Filters[i]; var o = other.Filters[i];
            if (f.FieldName != o.FieldName ||
                f.Operator != o.Operator ||
                !Equals(f.Value, o.Value)) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is ViewKey vk && Equals(vk);
    public override int GetHashCode() => _hashCode;
    public override string ToString() => Id;

    private int ComputeHash()
    {
        var hc = new HashCode();
        hc.Add(CollectionId);
        hc.Add(SortColumn);
        hc.Add(SortAscending);
        foreach (var f in Filters)
        {
            hc.Add(f.FieldName);
            hc.Add(f.Operator);
            hc.Add(f.Value);
        }
        return hc.ToHashCode();
    }
}


public sealed class SharedView
{
    public ViewKey Key { get; }

    private readonly ColumnarCollection _collection;
    private readonly int _sortFieldIndex;
    private readonly int[] _filterFieldIndexes;

    public SortIndex SortIndex { get; }

    private readonly ConcurrentDictionary<string, bool> _subscribers = new();

    public SharedView(ViewKey key, ColumnarCollection collection)
    {
        Key = key;
        _collection = collection;

        _sortFieldIndex = key.SortColumn is not null
            ? collection.Schema.GetFieldIndex(key.SortColumn)
            : -1;
        if (_sortFieldIndex < 0)
            _sortFieldIndex = collection.Schema.PrimaryKeyIndex;

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


    public int[] GetPageHandles(int startIndex, int pageSize) =>
        SortIndex.GetPageHandles(startIndex, pageSize, Key.Filters, _filterFieldIndexes);

    public int GetTotalCount() =>
        SortIndex.GetCount(Key.Filters, _filterFieldIndexes);


    public void NotifyUpsert(int handle, object? newSortValue) =>
        SortIndex.OnUpsert(handle, newSortValue);

    public void NotifyDelete(int handle) =>
        SortIndex.OnDelete(handle);
}
