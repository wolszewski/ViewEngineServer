using System.Text.Json.Serialization;
using LiveViewEngine.Core;

namespace ViewEngineServer.WebApp.Http.Dto;

public sealed class FieldDefinitionDto
{
    public string Name { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FieldType Type { get; set; } = FieldType.String;

    public bool IsPrimaryKey { get; set; }
    public bool IsSortable { get; set; }
    public bool IsFilterable { get; set; }
}