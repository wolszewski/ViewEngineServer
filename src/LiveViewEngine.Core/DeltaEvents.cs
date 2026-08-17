using System.Text.Json.Serialization;

namespace LiveViewEngine.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SnapshotEvent), "snapshot")]
[JsonDerivedType(typeof(RowUpdateEvent), "rowUpdate")]
[JsonDerivedType(typeof(RowInsertEvent), "rowInsert")]
[JsonDerivedType(typeof(RowRemoveEvent), "rowRemove")]
public abstract class DeltaEvent
{
    public required string ViewId { get; init; }
    public required int SubscriptionId { get; init; }
}

public sealed class SnapshotEvent : DeltaEvent
{
    public required int TotalCount { get; init; }
    public required int StartIndex { get; init; }
    public required IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows { get; init; }
}

public sealed class RowUpdateEvent : DeltaEvent
{
    public required string RowId { get; init; }
    public required int Position { get; init; }
    public required IReadOnlyDictionary<string, string?> ChangedFields { get; init; }
}

public sealed class RowInsertEvent : DeltaEvent
{
    public required int Position { get; init; }
    public required IReadOnlyDictionary<string, string?> Row { get; init; }
}

public sealed class RowRemoveEvent : DeltaEvent
{
    public required int Position { get; init; }
}
