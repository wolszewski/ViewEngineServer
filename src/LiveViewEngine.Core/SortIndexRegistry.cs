using System.Collections.Concurrent;
using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core;

internal readonly record struct SortIndexKey(string CollectionId, int FieldIndex);

internal sealed class SortIndexRegistry
{
    // Sentinel FieldIndex identifying the shared natural-order (no sortColumn) index per collection.
    internal const int NaturalOrderFieldIndex = -1;

    private readonly ConcurrentDictionary<SortIndexKey, IPositionIndex> _indexes = new();
    private readonly ConcurrentDictionary<SortIndexKey, DateTime> _flaggedForRemoval = new();

    internal IPositionIndex GetOrCreate(SortIndexKey key, RowCollection collection) =>
        _indexes.GetOrAdd(key, k => k.FieldIndex == NaturalOrderFieldIndex
            ? new NaturalOrderIndex(collection)
            : new SortIndex(collection, k.FieldIndex));

    internal bool TryGet(SortIndexKey key, out IPositionIndex? index) =>
        _indexes.TryGetValue(key, out index);

    internal IEnumerable<IPositionIndex> GetAllForCollection(string collectionId)
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

    internal void FlagForRemoval(SortIndexKey key) => _flaggedForRemoval.TryAdd(key, DateTime.UtcNow);

    internal void UnflagForRemoval(SortIndexKey key) => _flaggedForRemoval.TryRemove(key, out _);

    internal IEnumerable<(SortIndexKey Key, DateTime FlaggedAt)> GetFlagged()
    {
        foreach (var kv in _flaggedForRemoval)
        {
            yield return (kv.Key, kv.Value);
        }
    }

    internal int Count => _indexes.Count;
}

