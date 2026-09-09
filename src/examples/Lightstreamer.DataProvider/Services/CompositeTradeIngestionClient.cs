using LiveViewEngine.Poc.Shared;
using Microsoft.Extensions.Logging;

namespace Lightstreamer.DataProvider.Services;

// Fans out a single trade generator's ingestion calls to every registered adapter (two-level push
// and pure command mode), so the same generated data feeds both adapters and can be compared
// side by side just by switching the item subscribed from the UI.
public sealed class CompositeTradeIngestionClient(
    IReadOnlyList<ITradeIngestionClient> clients,
    ILogger<CompositeTradeIngestionClient> logger) : ITradeIngestionClient
{
    public async Task<bool> CreateCollectionAsync(
        string collectionName,
        IReadOnlyList<string> fieldNames,
        IReadOnlyList<string>? fieldTypes = null,
        CancellationToken cancellationToken = default)
    {
        var allSucceeded = true;
        foreach (var client in clients)
        {
            var succeeded = await client.CreateCollectionAsync(collectionName, fieldNames, fieldTypes, cancellationToken);
            if (!succeeded)
            {
                logger.LogWarning(
                    "CreateCollectionAsync failed for {CollectionName} on inner client {ClientType}.",
                    collectionName,
                    client.GetType().Name);
            }

            allSucceeded &= succeeded;
        }

        return allSucceeded;
    }

    public async Task<bool> IngestAsync(
        string collectionName,
        string rowKey,
        IReadOnlyDictionary<string, string?> fieldValues,
        CancellationToken cancellationToken = default)
    {
        var allSucceeded = true;
        foreach (var client in clients)
        {
            var succeeded = await client.IngestAsync(collectionName, rowKey, fieldValues, cancellationToken);
            if (!succeeded)
            {
                logger.LogWarning(
                    "IngestAsync failed for {CollectionName}/{RowKey} on inner client {ClientType}.",
                    collectionName,
                    rowKey,
                    client.GetType().Name);
            }

            allSucceeded &= succeeded;
        }

        return allSucceeded;
    }
}

