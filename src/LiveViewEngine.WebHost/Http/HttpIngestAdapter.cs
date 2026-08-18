using System.Text.Json;
using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;

namespace ViewEngineServer.WebApp.Http;

public static class HttpIngestAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<(IngestResult result, string? validationError)> HandleIngestAsync(
        string collectionName, HttpRequest request, IViewEngine engine, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(collectionName))
        {
            return (IngestResult.Fail("'collectionName' route value is required."), null);
        }

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
        {
            return (IngestResult.Fail("Request body is required."), null);
        }

        IngestCommand command;
        if (dto.Operation.Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(dto.PrimaryKeyValue))
            {
                return (IngestResult.Fail("'primaryKeyValue' is required for delete."), null);
            }

            command = new DeleteRowCommand
            {
                CollectionId = collectionName,
                Key = dto.PrimaryKeyValue
            };
        }
        else
        {
            if (!TryMapUpsertCommand(collectionName, dto, out var upsert, out var error))
            {
                return (IngestResult.Fail(error!), null);
            }

            command = upsert;
        }

        var result = await engine.IngestAsync(command, ct);
        return (result, null);
    }

    public static async Task<(IngestResult result, string? validationError)> HandleCreateCollectionAsync(HttpRequest httpRequest, IViewEngine engine, CancellationToken ct)
    {
        CreateCollectionRequest? request;
        try
        {
            request = await httpRequest.ReadFromJsonAsync<CreateCollectionRequest>(JsonOptions, ct);
        }
        catch (JsonException ex)
        {
            return (IngestResult.Fail("Invalid JSON body."), ex.Message);
        }

        if (request is null)
        {
            return (IngestResult.Fail("Request body is required."), null);
        }

        if (string.IsNullOrWhiteSpace(request.CollectionName))
        {
            return (IngestResult.Fail("'collectionName' is required."), null);
        }

        if (request.Fields.Count == 0)
        {
            return (IngestResult.Fail("At least one field must be defined."), null);
        }

        if (request.FieldTypes is { Count: > 0 } && request.FieldTypes.Count != request.Fields.Count)
        {
            return (IngestResult.Fail("'fieldTypes' count must match the number of fields."), null);
        }

        var schema = new CollectionSchema(
            request.CollectionName,
            request.Fields,
            request.FieldTypes is null ? null : ParseFieldTypes(request.FieldTypes));

        var command = new CreateCollectionCommand
        {
            CollectionId = request.CollectionName,
            Schema = schema
        };

        var result = await engine.IngestAsync(command, ct);
        return (result, null);
    }

    private static List<ScalarFieldType> ParseFieldTypes(IReadOnlyList<string> fieldTypes)
    {
        var parsed = new List<ScalarFieldType>(fieldTypes.Count);
        foreach (var fieldType in fieldTypes)
        {
           parsed.Add(ParseScalarFieldType(fieldType));
        }

        return parsed;
    }

    private static ScalarFieldType ParseScalarFieldType(string fieldType)
    {
        if (string.IsNullOrWhiteSpace(fieldType))
        {
           return ScalarFieldType.String;
        }

        return fieldType.Trim() switch
        {
           "string" => ScalarFieldType.String,
           "enum" => ScalarFieldType.String,
           "int" => ScalarFieldType.Int32,
           "int32" => ScalarFieldType.Int32,
           "long" => ScalarFieldType.Int64,
           "int64" => ScalarFieldType.Int64,
           "double" => ScalarFieldType.Double,
           "decimal" => ScalarFieldType.Decimal,
           "dateonly" => ScalarFieldType.DateOnly,
           "datetime" => ScalarFieldType.DateTime,
           "datetimeoffset" => ScalarFieldType.DateTimeOffset,
           _ => ScalarFieldType.String
        };
    }

    private static bool TryMapUpsertCommand(
        string collectionName,
        IngestRequestDto dto,
        out UpsertRowCommand command,
        out string? error)
    {
        error = null;
        var fields = dto.Fields is null
           ? new Dictionary<string, string?>()
           : new Dictionary<string, string?>(dto.Fields);

        var rowKey = dto.PrimaryKeyValue;
        if (string.IsNullOrWhiteSpace(rowKey))
        {
            if (!fields.TryGetValue("key", out rowKey) || string.IsNullOrWhiteSpace(rowKey))
            {
                fields.TryGetValue("id", out rowKey);
            }
        }

        if (string.IsNullOrWhiteSpace(rowKey))
        {
            command = null!;
            error = "'primaryKeyValue' is required for upsert when fields do not contain 'key'.";
            return false;
        }

        fields.Remove("key");
        fields.Remove("id");

        command = new UpsertRowCommand
        {
            CollectionId = collectionName,
            Key = rowKey,
            Fields = fields
        };
        return true;
    }
}
