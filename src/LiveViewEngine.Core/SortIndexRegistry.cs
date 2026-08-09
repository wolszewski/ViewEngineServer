using System.Collections.Concurrent;
using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core;

internal readonly record struct SortIndexKey(string CollectionId, int FieldIndex, bool Ascending);

internal sealed class SortIndexRegistry
{
    private readonly ConcurrentDictionary<SortIndexKey, SortIndex> _indexes = new();

    internal SortIndex GetOrCreate(SortIndexKey key, RowCollection collection) =>
        _indexes.GetOrAdd(key, k => new SortIndex(collection, k.FieldIndex, k.Ascending));

    internal IEnumerable<SortIndex> GetAllForCollection(string collectionId)
    {
        foreach (var kv in _indexes)
        {
            if (kv.Key.CollectionId == collectionId)
            {
                yield return kv.Value;
            }
        }
    }

    internal void Remove(SortIndexKey key) => _indexes.TryRemove(key, out _);
}
