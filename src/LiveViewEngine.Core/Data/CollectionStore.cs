using System.Collections.Concurrent;

namespace LiveViewEngine.Core.Data;

public interface ICollectionStore
{
    bool TryCreate(CollectionSchema schema);
    bool TryGet(string collectionId, out RowCollection? collection);
    bool TryGetRuntime(string collectionId, out CollectionRuntime? runtime);
    ICollection<string> CollectionIds { get; }
}

public sealed class CollectionStore : ICollectionStore
{
    private readonly ConcurrentDictionary<string, CollectionRuntime> _collections = new();

    public bool TryCreate(CollectionSchema schema)
    {
        var runtime = new CollectionRuntime(schema.CollectionName, new RowCollection(schema));
        return _collections.TryAdd(schema.CollectionName, runtime);
    }

    public bool TryGet(string collectionId, out RowCollection? collection)
    {
        var found = TryGetRuntime(collectionId, out var runtime);
        collection = runtime?.Collection;
        return found;
    }

    public bool TryGetRuntime(string collectionId, out CollectionRuntime? runtime) =>
        _collections.TryGetValue(collectionId, out runtime);

    public ICollection<string> CollectionIds => _collections.Keys;
}
