using System.Collections.Concurrent;
using ViewEngineServer.Core.Indexing;
using ViewEngineServer.Core.Storage;

namespace ViewEngineServer.Core.Views;

// ---------------------------------------------------------------------------
// View definition & canonical key
// ---------------------------------------------------------------------------

/// <summary>
/// Transport-neutral description of a view: which collection, how to sort,
/// and which filters to apply. Produced by any adapter that wants to subscribe.
/// </summary>
public sealed class ViewDefinition
{
    public required string CollectionId { get; init; }
    public string? SortColumn { get; init; }
    public bool SortAscending { get; init; } = true;
    public IReadOnlyList<FilterSpec> Filters { get; init; } = [];
}

/// <summary>
/// Canonical, equality-comparable key derived from a <see cref="ViewDefinition"/>.
/// Two clients whose requests produce the same key will share one server-side view.
/// </summary>
public sealed class ViewKey : IEquatable<ViewKey>
{
    public string CollectionId { get; }
    public string? SortColumn { get; }
    public bool SortAscending { get; }
    public IReadOnlyList<FilterSpec> Filters { get; }

    /// <summary>Human-readable stable identifier used in outbound messages.</summary>
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

// ---------------------------------------------------------------------------
// Shared view — one per unique ViewKey
// ---------------------------------------------------------------------------

/// <summary>
/// Server-side materialised view shared by all subscribers with the same
/// <see cref="ViewKey"/>. Owns the <see cref="SortIndex"/> for its sort column
/// and tracks which connections are subscribed.
/// </summary>
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

    // -----------------------------------------------------------------------
    // Query helpers
    // -----------------------------------------------------------------------

    public int[] GetPageHandles(int startIndex, int pageSize) =>
        SortIndex.GetPageHandles(startIndex, pageSize, Key.Filters, _filterFieldIndexes);

    public int GetTotalCount() =>
        SortIndex.GetCount(Key.Filters, _filterFieldIndexes);

    // -----------------------------------------------------------------------
    // Mutation notification
    // -----------------------------------------------------------------------

    public void NotifyUpsert(int handle, object? newSortValue) =>
        SortIndex.OnUpsert(handle, newSortValue);

    public void NotifyDelete(int handle) =>
        SortIndex.OnDelete(handle);
}
