namespace ViewEngineServer.Http.Dto;

public sealed class CreateCollectionRequestDto
{
    public string? CollectionId { get; set; }
    public int Capacity { get; set; } = 100_000;
    public List<FieldDefinitionDto> Fields { get; set; } = [];
}