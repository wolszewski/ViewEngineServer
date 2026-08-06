namespace ViewEngineServer.WebApp.Http.Dto;

public sealed class CreateCollectionRequest
{
    public required string CollectionId { get; set; }
    public required List<string> Fields { get; init; } = [];
    public required string PrimaryKey { get; init; }
}