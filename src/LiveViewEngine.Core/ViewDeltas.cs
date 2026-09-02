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

    /// <summary>
    /// True when this snapshot carries no rows because the requested viewport is already fully
    /// covered by what the client previously received (e.g. an unchanged/contained viewport
    /// re-request used to flush buffered live deltas). Distinguishes "you already have this data"
    /// from a genuine full refresh that happens to match zero rows.
    /// </summary>
    public bool NoChanges { get; init; }
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
