using LiveViewEngine.Collections;
using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core;

// Cheap default ordering used when a view has no sortColumn: no field reads/typed conversions,
// no pending-old-value bookkeeping (position never depends on field content), and no reordering
// on update — a row is assigned a position once, on first upsert, and keeps it until deleted.
//
// Order (including for rows already in the RowCollection when this index is constructed) always
// matches true insertion order: sequence numbers are read from RowCollection.GetArrivalSequence,
// which is assigned once per row at true first-insertion time and is stable across delete+reinsert
// slot reuse — unlike RowCollection's live-index (dictionary) enumeration order, which is not.
public sealed class NaturalOrderIndex : IPositionIndex
{
    private readonly RowCollection _collection;
    private readonly NodeArrayTree<SequenceComparer> _tree;
    private volatile int _subscriberCount;
    private long _lastUsedTicks = DateTime.UtcNow.Ticks;

    public NaturalOrderIndex(RowCollection collection)
    {
        _collection = collection;
        _tree = new NodeArrayTree<SequenceComparer>(new SequenceComparer(this));

        foreach (var kv in collection.GetAllLiveIndexes())
        {
            _tree.Insert(kv.Value);
        }
    }

    public int Count => _tree.Count;

    // No field drives this order; -1 means "not associated with a field".
    public int FieldIndex => -1;

    public int SubscriberCount => _subscriberCount;
    public DateTime LastUsedUtc => new DateTime(Interlocked.Read(ref _lastUsedTicks), DateTimeKind.Utc);

    // Explicit IPositionIndex implementations below: same rationale as SortIndex - these lifecycle
    // operations exist solely to satisfy the internal runtime contract, not as NaturalOrderIndex's
    // own public API.
    void IPositionIndex.IncrementSubscribers()
    {
        Interlocked.Increment(ref _subscriberCount);
        Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);
    }

    void IPositionIndex.DecrementSubscribers() => Interlocked.Decrement(ref _subscriberCount);

    bool IPositionIndex.AffectsOrder(in FieldMask changedMask) => false;

    void IPositionIndex.CaptureOldValue(int rowIndex)
    {
    }

    void IPositionIndex.ResetPending()
    {
    }

    int IPositionIndex.IndexOfWithPendingOldValue(int rowIndex) => _tree.IndexOf(rowIndex);

    TResult IPositionIndex.WithPendingOldValue<TResult>(int rowIndex, Func<TResult> action) => action();

    public void OnUpsert(int rowIndex)
    {
        if (_tree.Contains(rowIndex))
        {
            // Existing row: position never changes in response to field updates.
            return;
        }

        _tree.Insert(rowIndex);
    }

    public void OnDelete(int rowIndex)
    {
        _tree.TryDelete(rowIndex);
    }

    IMutableRowIndex IPositionIndex.CreateFilteredIndex(FilterSet filters) => CreateFilteredIndex(filters);

    internal IMutableRowIndex CreateFilteredIndex(FilterSet filters) =>
        new FilteredDataIndex<SequenceComparer>(new SequenceComparer(this), EnumerateFiltered(filters));

    public int IndexOf(int rowIndex) => _tree.IndexOf(rowIndex);

    public int GetByIndex(int index) => _tree.GetByIndex(index);

    public void Take(int startIndex, Span<int> destination) => _tree.Take(startIndex, destination);

    public void TakeReverse(int startIndex, Span<int> destination) => _tree.TakeReverse(startIndex, destination);

    private IEnumerable<int> EnumerateFiltered(FilterSet filters)
    {
        var cursor = _tree.GetCursor(0);
        while (cursor.MoveNext())
        {
            int index = cursor.Current;
            if (filters.Passes(_collection, index))
            {
                yield return index;
            }
        }
    }

    internal readonly struct SequenceComparer : IComparer<int>
    {
        private readonly NaturalOrderIndex _owner;

        internal SequenceComparer(NaturalOrderIndex owner) => _owner = owner;

        public int Compare(int x, int y) =>
            _owner._collection.GetArrivalSequence(x).CompareTo(_owner._collection.GetArrivalSequence(y));
    }
}
