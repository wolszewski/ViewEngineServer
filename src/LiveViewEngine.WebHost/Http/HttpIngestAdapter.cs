using System.Text.Json;
using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;
using ViewEngineServer.WebApp.Http.Dto;

namespace ViewEngineServer.WebApp.Http;

public static class HttpIngestAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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
        {
            return (IngestResult.Fail("Request body is required."), null);
        }

        if (string.IsNullOrWhiteSpace(dto.CollectionId))
        {
            return (IngestResult.Fail("'collectionId' is required."), null);
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
                CollectionId = dto.CollectionId,
                Key = dto.PrimaryKeyValue
            };
        }
        else
        {
            if (!TryMapUpsertCommand(dto, out var upsert, out var error))
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
            return (IngestResult.Fail("'collectionId' is required."), null);
        }

        if (request.Fields.Count == 0)
        {
            return (IngestResult.Fail("At least one field must be defined."), null);
        }

        var schema = new CollectionSchema(request.CollectionName, request.Fields);

        var command = new CreateCollectionCommand
        {
            CollectionId = request.CollectionName,
            Schema = schema
        };

        var result = await engine.IngestAsync(command, ct);
        return (result, null);
    }

    private static bool TryMapUpsertCommand(
        IngestRequestDto dto,
        out UpsertRowCommand command,
        out string? error)
    {
        error = null;
        var fields = new Dictionary<string, string?>();
        if (dto.Fields is not null)
        {
            foreach (var (key, element) in dto.Fields)
            {
                fields[key] = element.ValueKind == JsonValueKind.Null ? null : element.GetString() ?? element.GetRawText();
            }
        }

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

        command = new UpsertRowCommand
        {
            CollectionId = dto.CollectionId!,
            Key = rowKey,
            Fields = fields
        };
        return true;
    }
}
