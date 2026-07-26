using System.Collections.Concurrent;
using ViewEngineServer.Core.Schema;

namespace ViewEngineServer.Core.Storage;

public interface ICollectionStore
{
    /// <summary>Create a new collection. Returns false if the id already exists.</summary>
    bool TryCreate(CollectionSchema schema);

    /// <summary>Look up an existing collection by id.</summary>
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
