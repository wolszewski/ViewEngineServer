namespace LiveViewEngine.Core.DataIngest;

public sealed class UpsertRowCommand : IngestCommand
{
    public required string Key { get; init; }
    public required IReadOnlyDictionary<string, string?> Fields { get; init; }
}