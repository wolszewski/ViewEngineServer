using System.Text.Json.Serialization;

namespace ViewEngineServer.Core.Delta;

// ---------------------------------------------------------------------------
// Polymorphic event hierarchy — no HTTP / WebSocket dependencies
// ---------------------------------------------------------------------------

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SnapshotEvent),  "snapshot")]
[JsonDerivedType(typeof(RowUpdateEvent), "rowUpdate")]
[JsonDerivedType(typeof(RowInsertEvent), "rowInsert")]
[JsonDerivedType(typeof(RowRemoveEvent), "rowRemove")]
public abstract class DeltaEvent
{
    /// <summary>Identifies the shared view this event belongs to.</summary>
    public required string ViewId { get; init; }
}

/// <summary>
/// Full initial snapshot of the requested viewport page.
/// Sent when a client first subscribes or changes its viewport range.
/// </summary>
public sealed class SnapshotEvent : DeltaEvent
{
    public required int TotalCount { get; init; }
    public required int StartIndex { get; init; }
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }
}

/// <summary>
/// One or more fields within a visible row changed value.
/// The row did not move outside the viewport.
/// </summary>
public sealed class RowUpdateEvent : DeltaEvent
{
    public required string RowId { get; init; }
    public required int Position { get; init; }
    public required IReadOnlyDictionary<string, object?> ChangedFields { get; init; }
}

/// <summary>A row has entered the visible viewport at the given position.</summary>
public sealed class RowInsertEvent : DeltaEvent
{
    public required int Position { get; init; }
    public required IReadOnlyDictionary<string, object?> Row { get; init; }
}

/// <summary>The row at the given position has left the visible viewport.</summary>
public sealed class RowRemoveEvent : DeltaEvent
{
    public required int Position { get; init; }
}
