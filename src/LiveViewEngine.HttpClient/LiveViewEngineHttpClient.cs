using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LiveViewEngine.HttpClient;

public sealed class LiveViewEngineHttpClient(System.Net.Http.HttpClient httpClient, ILogger<LiveViewEngineHttpClient>? logger = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<bool> CreateCollectionAsync(
        string collectionName,
        IReadOnlyCollection<string> fieldNames,
        IReadOnlyCollection<string>? fieldTypes = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        var request = new
        {
            collectionName,
            fields = fieldNames.ToList(),
            fieldTypes = fieldTypes?.ToList() ?? []
        };
        var response = await httpClient.PostAsJsonAsync("/collections", request, JsonOptions, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger?.LogWarning("Create collection failed ({StatusCode}): {Body}", response.StatusCode, body);
            return false;
        }

        return true;
    }

    public async Task<bool> IngestAsync(string collectionName, string primaryKeyValue, IReadOnlyDictionary<string, string?> fields, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryKeyValue);

        var request = new
        {
            operation = "upsert",
            primaryKeyValue,
            fields
        };

        var response = await httpClient.PostAsJsonAsync($"/collections/{Uri.EscapeDataString(collectionName)}/ingest", request, JsonOptions, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger?.LogWarning("Ingest failed ({StatusCode}): {Body}", response.StatusCode, body);
            return false;
        }

        return true;
    }
}
