using System.Collections.Concurrent;
using LiveViewEngine.Core;
using LiveViewEngine.Core.Runtime;

namespace LiveViewEngine.Core.Data;

public interface ICollectionStore
{
    bool TryCreate(CollectionSchema schema);
    bool TryGet(string collectionId, out RowCollection? collection);
    bool TryGetRuntime(string collectionId, out CollectionRuntime? runtime);
    bool TryGetSchema(string collectionId, out CollectionSchema? schema);
    ICollection<string> CollectionIds { get; }
}

public sealed class CollectionStore(IViewEngineMetrics? metrics, LiveViewEngineOptions? options = null) : ICollectionStore
{
    private readonly ConcurrentDictionary<string, CollectionRuntime> _collections = new();

    public bool TryCreate(CollectionSchema schema)
    {
        var collection = new RowCollection(schema);
        var runtime = new CollectionRuntime(collection, metrics, options);
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

    public bool TryGetSchema(string collectionId, out CollectionSchema? schema)
    {
        if (TryGetRuntime(collectionId, out var runtime) && runtime is not null)
        {
            schema = runtime.Collection.Schema;
            return true;
        }

        schema = null;
        return false;
    }

    public ICollection<string> CollectionIds => _collections.Keys;
}
