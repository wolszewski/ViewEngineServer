using System.Collections.Concurrent;

namespace LiveViewEngine.Core.Data;

public interface ICollectionStore
{
    bool TryCreate(CollectionSchema schema);
    bool TryGet(string collectionId, out RowCollection? collection);
    ICollection<string> CollectionIds { get; }
}

public sealed class CollectionStore : ICollectionStore
{
    private readonly ConcurrentDictionary<string, RowCollection> _collections = new();

    public bool TryCreate(CollectionSchema schema)
    {
        var col = new RowCollection(schema);
        return _collections.TryAdd(schema.CollectionName, col);
    }

    public bool TryGet(string collectionId, out RowCollection? collection)
    {
        var found = _collections.TryGetValue(collectionId, out var rowCollection);
        collection = rowCollection;
        return found;
    }

    public ICollection<string> CollectionIds => _collections.Keys;
}
