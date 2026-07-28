using ViewEngineServer.Core.Schema;

namespace ViewEngineServer.Core.Ingestion;

// ---------------------------------------------------------------------------
// Result
// ---------------------------------------------------------------------------

public sealed class IngestResult
{
    public bool Success { get; private init; }
    public string? Error { get; private init; }

    public static IngestResult Ok() => new() { Success = true };
    public static IngestResult Fail(string error) => new() { Success = false, Error = error };
}

// ---------------------------------------------------------------------------
// Commands — transport-neutral; produced by any adapter (HTTP, TCP, Kafka…)
// ---------------------------------------------------------------------------

public abstract class IngestCommand
{
    public required string CollectionId { get; init; }
}

/// <summary>Registers a new collection with its schema.</summary>
public sealed class CreateCollectionCommand : IngestCommand
{
    public required CollectionSchema Schema { get; init; }
}

/// <summary>
/// Insert or update a row identified by its primary key.
/// Fields not present in the dictionary are left unchanged for existing rows.
/// </summary>
public sealed class UpsertRowCommand : IngestCommand
{
    public required IReadOnlyDictionary<string, object?> Fields { get; init; }
}

/// <summary>Logically delete the row with the given primary-key value.</summary>
public sealed class DeleteRowCommand : IngestCommand
{
    public required string PrimaryKeyValue { get; init; }
}
