namespace LiveViewEngine.Poc.Shared;

public interface ITradeIngestionClient
{
    Task<bool> CreateCollectionAsync(string collectionName, IReadOnlyList<string> fieldNames, CancellationToken cancellationToken = default);
    Task<bool> IngestAsync(string collectionName, string rowKey, IReadOnlyDictionary<string, string?> fieldValues, CancellationToken cancellationToken = default);
}
