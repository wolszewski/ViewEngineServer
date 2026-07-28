using System.Text.Json.Serialization;

namespace ViewEngineServer.WebSocket.Dto;

public sealed class WsFilterDto
{
    public string Field { get; set; } = string.Empty;

    public string Operator { get; set; } = "eq";
    [JsonConverter(typeof(JsonObjectConverter))]
    public object? Value { get; set; }
}