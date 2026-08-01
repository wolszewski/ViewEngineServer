using System.Collections.Concurrent;

namespace LiveViewEngine.Core;

public interface ICollectionStore
{
    bool TryCreate(CollectionSchema schema);
    bool TryGet(string collectionId, out ColumnarCollection? collection);
    IReadOnlyList<string> CollectionIds { get; }
}

public sealed class CollectionStore : ICollectionStore
{
    private readonly ConcurrentDictionary<string, ColumnarCollection> _collections = new();

    public bool TryCreate(CollectionSchema schema)
    {
        var col = new ColumnarCollection(schema);
        return _collections.TryAdd(schema.CollectionId, col);
    }

    public bool TryGet(string collectionId, out ColumnarCollection? collection)
    {
        var found = _collections.TryGetValue(collectionId, out var c);
        collection = c;
        return found;
    }

    public IReadOnlyList<string> CollectionIds => [.. _collections.Keys];
}
