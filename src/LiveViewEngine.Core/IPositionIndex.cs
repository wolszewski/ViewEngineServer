namespace LiveViewEngine.Core;

// Generalizes SortIndex's position-tracking contract so SharedView/MutationPropagator can operate
// over any ordering strategy (field-sorted, natural/insertion order, ...) without depending on a
// concrete index type. FieldIndex is -1 for indexes whose order does not depend on any field value.
internal interface IPositionIndex : IRowIndex
{
    int FieldIndex { get; }
    int SubscriberCount { get; }
    DateTime LastUsedUtc { get; }

    void IncrementSubscribers();
    void DecrementSubscribers();

    // True if a mutation touching these fields can change this row's position.
    bool AffectsOrder(in FieldMask changedMask);

    void CaptureOldValue(int rowIndex);
    void ResetPending();
    int IndexOfWithPendingOldValue(int rowIndex);
    TResult WithPendingOldValue<TResult>(int rowIndex, Func<TResult> action);

    void OnUpsert(int rowIndex);
    void OnDelete(int rowIndex);

    IMutableRowIndex CreateFilteredIndex(FilterSet filters);
}
