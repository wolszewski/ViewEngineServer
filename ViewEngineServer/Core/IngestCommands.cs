namespace ViewEngineServer.Core;


public sealed class IngestResult
{
    public bool Success { get; private init; }
    public string? Error { get; private init; }

    public static IngestResult Ok() => new() { Success = true };
    public static IngestResult Fail(string error) => new() { Success = false, Error = error };
}


public abstract class IngestCommand
{
    public required string CollectionId { get; init; }
}

public sealed class CreateCollectionCommand : IngestCommand
{
    public required CollectionSchema Schema { get; init; }
}

public sealed class UpsertRowCommand : IngestCommand
{
    public required IReadOnlyDictionary<string, object?> Fields { get; init; }
}

public sealed class DeleteRowCommand : IngestCommand
{
    public required string PrimaryKeyValue { get; init; }
}
