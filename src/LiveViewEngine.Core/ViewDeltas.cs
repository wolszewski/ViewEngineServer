using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core;

public abstract class ViewDelta
{
    public required string ViewId { get; init; }
    public IReadOnlyList<int>? VisibleFieldIndexes { get; init; }
}

public sealed class SnapshotDelta : ViewDelta
{
    public required CollectionSchema Schema { get; init; }
    public required int TotalCount { get; init; }
    public required int StartIndex { get; init; }
    public required IReadOnlyList<string?[]> Rows { get; init; }
    public bool IsPartial { get; init; }
}

public sealed class SnapshotStartDelta : ViewDelta
{
    public required CollectionSchema Schema { get; init; }
    public required int TotalCount { get; init; }
    public required int StartIndex { get; init; }
    public bool IsPartial { get; init; }
}

public sealed class SnapshotRowsDelta : ViewDelta
{
    public required CollectionSchema Schema { get; init; }
    public required int StartRowNumber { get; init; }
    public required IReadOnlyList<string?[]> Rows { get; init; }
    public bool IsPartial { get; init; }
}

public sealed class EndOfSnapshotDelta : ViewDelta
{
}

// Returned instead of a normal snapshot when a subscribe targets a collection that doesn't exist. This is an
// expected, externally-triggerable outcome (bad client input or a subscribe racing a concurrent create), not an
// exceptional condition, so it is signalled as data rather than thrown.
public sealed class SubscriptionRejectedDelta : ViewDelta
{
    public required string CollectionId { get; init; }
}

public sealed class RowUpdateDelta : ViewDelta
{
    public required CollectionSchema Schema { get; init; }
    public required string RowId { get; init; }
    public required int Position { get; init; }
    public required IReadOnlyCollection<KeyValuePair<int, string?>> ChangedColumns { get; init; }
}

public sealed class RowInsertDelta : ViewDelta
{
    public required CollectionSchema Schema { get; init; }
    public required int Position { get; init; }
    public required string?[] Row { get; init; }
}

public sealed class RowRemoveDelta : ViewDelta
{
    public required string RowId { get; init; }
    public required int Position { get; init; }
}

public sealed class RowReplaceDelta : ViewDelta
{
    public required CollectionSchema Schema { get; init; }
    public required string RemovedRowId { get; init; }
    public required int RemovePosition { get; init; }
    public required int InsertPosition { get; init; }
    public required string?[] Row { get; init; }
}
