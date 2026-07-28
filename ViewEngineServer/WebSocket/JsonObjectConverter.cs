using System.Text.Json;
using System.Text.Json.Serialization;

namespace ViewEngineServer.WebSocket;

public sealed class JsonObjectConverter : JsonConverter<object?>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number =>
                reader.TryGetInt32(out var i) ? i :
                reader.TryGetInt64(out var l) ? l :
                (object?)reader.GetDouble(),
            _ => reader.GetString()
        };

    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, options);
}