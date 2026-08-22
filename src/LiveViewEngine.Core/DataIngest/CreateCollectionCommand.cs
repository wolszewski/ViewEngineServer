using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core.DataIngest;

public sealed class CreateCollectionCommand : IngestCommand
{
    public required CollectionSchema Schema { get; init; }
}