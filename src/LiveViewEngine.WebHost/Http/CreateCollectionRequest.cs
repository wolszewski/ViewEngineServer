namespace ViewEngineServer.WebApp.Http;

public sealed class CreateCollectionRequest
{
    public required string CollectionName { get; set; }
    public required List<string> Fields { get; init; } = [];
}