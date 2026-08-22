namespace LiveViewEngine.Core.DataIngest;

public abstract class IngestCommand
{
    public required string CollectionId { get; init; }
}