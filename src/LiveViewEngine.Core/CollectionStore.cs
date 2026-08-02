using System.Collections.Concurrent;

namespace LiveViewEngine.Core;

public interface ICollectionStore
{
    bool TryCreate(CollectionSchema schema);
    bool TryGet(string collectionId, out RowCollection? collection);
    IReadOnlyList<string> CollectionIds { get; }
}

public sealed class CollectionStore : ICollectionStore
{
    private readonly ConcurrentDictionary<string, RowCollection> _collections = new();

    public bool TryCreate(CollectionSchema schema)
    {
        var col = new RowCollection(schema);
        return _collections.TryAdd(schema.CollectionId, col);
    }

    public bool TryGet(string collectionId, out RowCollection? collection)
    {
        var found = _collections.TryGetValue(collectionId, out var c);
        collection = c;
        return found;
    }

    public IReadOnlyList<string> CollectionIds => [.. _collections.Keys];
}
