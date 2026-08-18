namespace LiveViewEngine.Poc.Shared;

public interface ITradeIngestionClient
{
    Task<bool> CreateCollectionAsync(
        string collectionName,
        IReadOnlyList<string> fieldNames,
        IReadOnlyList<string>? fieldTypes = null,
        CancellationToken cancellationToken = default);
    Task<bool> IngestAsync(string collectionName, string rowKey, IReadOnlyDictionary<string, string?> fieldValues, CancellationToken cancellationToken = default);
}
