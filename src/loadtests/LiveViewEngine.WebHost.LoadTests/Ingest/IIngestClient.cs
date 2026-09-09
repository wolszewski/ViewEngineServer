namespace LiveViewEngine.WebHost.LoadTests.Ingest;

/// <summary>
/// Ingest transport abstraction so load test scenarios can switch between HTTP and TCP
/// ingestion without changing scenario code (LoadTest:IngestMode / --ingest=http|tcp).
/// </summary>
public interface IIngestClient
{
    Task<bool> CreateCollectionAsync(
        string collectionName,
        IReadOnlyList<string> fieldNames,
        IReadOnlyList<string>? fieldTypes = null,
        CancellationToken ct = default);

    Task<bool> IngestAsync(
        string collectionName,
        string primaryKeyValue,
        IReadOnlyDictionary<string, string?> fields,
        CancellationToken ct = default);
}
