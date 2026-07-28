using System.Text.Json;

namespace ViewEngineServer.Http;

public sealed class IngestRequestDto
{
    public string Operation { get; set; } = "upsert";
    public string? CollectionId { get; set; }

    public Dictionary<string, JsonElement>? Fields { get; set; }

    public string? PrimaryKeyValue { get; set; }
}