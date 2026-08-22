namespace LiveViewEngine.Core.DataIngest;

public sealed class DeleteRowCommand : IngestCommand
{
    public required string Key { get; init; }
}