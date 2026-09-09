using LiveViewEngine.HttpClient;

namespace LiveViewEngine.WebHost.LoadTests.Ingest;

public sealed class HttpIngestClient(LiveViewEngineHttpClient client) : IIngestClient
{
    public Task<bool> CreateCollectionAsync(
        string collectionName,
        IReadOnlyList<string> fieldNames,
        IReadOnlyList<string>? fieldTypes = null,
        CancellationToken ct = default) =>
        client.CreateCollectionAsync(collectionName, fieldNames, fieldTypes, ct);

    public Task<bool> IngestAsync(
        string collectionName,
        string primaryKeyValue,
        IReadOnlyDictionary<string, string?> fields,
        CancellationToken ct = default) =>
        client.IngestAsync(collectionName, primaryKeyValue, fields, ct);
}
