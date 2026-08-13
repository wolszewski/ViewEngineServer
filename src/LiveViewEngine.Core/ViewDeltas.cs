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
    public required int Position { get; init; }
}
