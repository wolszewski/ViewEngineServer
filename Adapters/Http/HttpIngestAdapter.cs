using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using ViewEngineServer.Core.Engine;
using ViewEngineServer.Core.Ingestion;
using ViewEngineServer.Core.Schema;

namespace ViewEngineServer.Adapters.Http;

// ---------------------------------------------------------------------------
// DTOs — only used at the HTTP boundary
// ---------------------------------------------------------------------------

public sealed class IngestRequestDto
{
    /// <summary>"upsert" (default) | "delete"</summary>
    public string Operation { get; set; } = "upsert";
    public string? CollectionId { get; set; }

    /// <summary>Field name → JSON value. Used for upsert operations.</summary>
    public Dictionary<string, JsonElement>? Fields { get; set; }

    /// <summary>Primary-key value to delete. Used for delete operations.</summary>
    public string? PrimaryKeyValue { get; set; }
}

public sealed class CreateCollectionRequestDto
{
    public string? CollectionId { get; set; }
    public int Capacity { get; set; } = 100_000;
    public List<FieldDefinitionDto> Fields { get; set; } = [];
}

public sealed class FieldDefinitionDto
{
    public string Name { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FieldType Type { get; set; } = FieldType.String;

    public bool IsPrimaryKey { get; set; }
    public bool IsSortable { get; set; }
    public bool IsFilterable { get; set; }
}

// ---------------------------------------------------------------------------
// Adapter — maps HTTP request bodies to transport-neutral ingest commands
// ---------------------------------------------------------------------------

public static class HttpIngestAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Parse a POST /ingest request body and forward the resulting command to
    /// the engine. Returns (result, validationError).
    /// </summary>
    public static async Task<(IngestResult result, string? validationError)> HandleIngestAsync(
        HttpRequest request, IViewEngine engine, CancellationToken ct)
    {
        IngestRequestDto? dto;
        try
        {
            dto = await request.ReadFromJsonAsync<IngestRequestDto>(JsonOptions, ct);
        }
        catch (JsonException ex)
        {
            return (IngestResult.Fail("Invalid JSON body."), ex.Message);
        }

        if (dto is null)
            return (IngestResult.Fail("Request body is required."), null);
        if (string.IsNullOrWhiteSpace(dto.CollectionId))
            return (IngestResult.Fail("'collectionId' is required."), null);

        IngestCommand command;
        if (dto.Operation.Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(dto.PrimaryKeyValue))
                return (IngestResult.Fail("'primaryKeyValue' is required for delete."), null);
            command = new DeleteRowCommand
            {
                CollectionId = dto.CollectionId,
                PrimaryKeyValue = dto.PrimaryKeyValue
            };
        }
        else
        {
            command = MapUpsertCommand(dto);
        }

        var result = await engine.IngestAsync(command, ct);
        return (result, null);
    }

    /// <summary>
    /// Parse a POST /collections request body and register the collection schema.
    /// </summary>
    public static async Task<(IngestResult result, string? validationError)> HandleCreateCollectionAsync(
        HttpRequest request, IViewEngine engine, CancellationToken ct)
    {
        CreateCollectionRequestDto? dto;
        try
        {
            dto = await request.ReadFromJsonAsync<CreateCollectionRequestDto>(JsonOptions, ct);
        }
        catch (JsonException ex)
        {
            return (IngestResult.Fail("Invalid JSON body."), ex.Message);
        }

        if (dto is null)
            return (IngestResult.Fail("Request body is required."), null);
        if (string.IsNullOrWhiteSpace(dto.CollectionId))
            return (IngestResult.Fail("'collectionId' is required."), null);
        if (dto.Fields.Count == 0)
            return (IngestResult.Fail("At least one field must be defined."), null);
        if (!dto.Fields.Any(f => f.IsPrimaryKey))
            return (IngestResult.Fail("Exactly one field must be marked 'isPrimaryKey'."), null);

        var schema = new CollectionSchema
        {
            CollectionId = dto.CollectionId,
            Capacity = dto.Capacity,
            Fields = dto.Fields.Select(f =>
                new FieldDefinition(f.Name, f.Type, f.IsPrimaryKey, f.IsSortable, f.IsFilterable))
                .ToList()
        };

        var cmd = new CreateCollectionCommand
        {
            CollectionId = dto.CollectionId,
            Schema = schema
        };

        var result = await engine.IngestAsync(cmd, ct);
        return (result, null);
    }

    private static UpsertRowCommand MapUpsertCommand(IngestRequestDto dto)
    {
        var fields = new Dictionary<string, object?>();
        if (dto.Fields is not null)
        {
            foreach (var (key, element) in dto.Fields)
                fields[key] = UnboxJsonElement(element);
        }
        return new UpsertRowCommand
        {
            CollectionId = dto.CollectionId!,
            Fields = fields
        };
    }

    private static object? UnboxJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True    => (object?)true,
        JsonValueKind.False   => false,
        JsonValueKind.Null    => null,
        JsonValueKind.String  => element.GetString(),
        JsonValueKind.Number  => UnboxNumber(element),
        _                     => element.GetRawText() // Array, Object, Undefined — store as raw JSON text
    };

    private static object? UnboxNumber(JsonElement element)
    {
        if (element.TryGetInt32(out var i)) return i;
        if (element.TryGetInt64(out var l)) return l;
        return element.GetDouble();
    }
}
