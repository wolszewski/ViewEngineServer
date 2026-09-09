using System.Collections;
using Lightstreamer.Interfaces.Data;
using LiveViewEngine.Poc.Shared;
using Microsoft.Extensions.Logging;

namespace Lightstreamer.DataProvider.Services;

public sealed class TradeMergeDataProvider(
    TradeCommandProvider commandProvider,
    ILogger<TradeMergeDataProvider> logger) : IDataProvider, ITradeIngestionClient
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Dictionary<string, string?>> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _subscribedItems = new(StringComparer.OrdinalIgnoreCase);
    private IItemEventListener? _listener;
    private long _forwardedUpdateCount;
    private long _suppressedUpdateCount;

    public void Init(IDictionary parameters, string configFile)
    {
    }

    public void SetListener(IItemEventListener eventListener)
    {
        _listener = eventListener;
        logger.LogInformation("Trade merge adapter listener attached.");
    }

    public bool IsSnapshotAvailable(string itemName) => true;

    public void Subscribe(string itemName)
    {
        lock (_sync)
        {
            _subscribedItems.Add(itemName);
        }

        logger.LogInformation("Trade merge adapter subscribed item {ItemName}.", itemName);
        SendSnapshot(itemName);
    }

    public void Unsubscribe(string itemName)
    {
        lock (_sync)
        {
            _subscribedItems.Remove(itemName);
        }

        logger.LogInformation("Trade merge adapter unsubscribed item {ItemName}.", itemName);
    }

    public Task<bool> CreateCollectionAsync(string collectionName, IReadOnlyList<string> fieldNames, IReadOnlyList<string>? fieldTypes = null, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "CreateCollectionAsync called for {CollectionName} with {FieldCount} fields.",
            collectionName,
            fieldNames.Count);
        return Task.FromResult(true);
    }

    public Task<bool> IngestAsync(string collectionName, string rowKey, IReadOnlyDictionary<string, string?> fieldValues, CancellationToken cancellationToken = default)
    {
        bool isNew;
        bool shouldForward;
        long forwardedUpdateCount = 0;
        long suppressedUpdateCount = 0;
        lock (_sync)
        {
            isNew = !_rows.TryGetValue(rowKey, out var existing);
            if (isNew)
            {
                _rows[rowKey] = new Dictionary<string, string?>(fieldValues, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                // Merge into the cached row instead of replacing it - update-mode ingestion only
                // carries the handful of changed fields, and replacing the cache with just those
                // would permanently drop every other field the row previously had. Any later
                // second-level subscribe (SendSnapshot) reads this cache, so a clobbered cache
                // meant a client subscribing after an update landed only ever saw those few
                // fields instead of the full row.
                foreach (var (field, value) in fieldValues)
                {
                    existing![field] = value;
                }
            }

            shouldForward = _listener is not null && _subscribedItems.Contains(rowKey);
            if (shouldForward)
            {
                _forwardedUpdateCount++;
                forwardedUpdateCount = _forwardedUpdateCount;
            }
            else
            {
                _suppressedUpdateCount++;
                suppressedUpdateCount = _suppressedUpdateCount;
            }
        }

        if (isNew)
        {
            commandProvider.NotifyKeyAdded(rowKey);
        }

        if (shouldForward && _listener is not null)
        {
            // Forward only the incoming delta, not the full cached row - correct and cheaper for a
            // MERGE-mode item, which keeps unchanged fields from the last update/snapshot as-is.
            var delta = new Dictionary<string, string?>(fieldValues, StringComparer.OrdinalIgnoreCase);
            _listener.Update(rowKey, delta, isSnapshot: false);

            if (forwardedUpdateCount <= 5 || forwardedUpdateCount % 1_000 == 0)
            {
                logger.LogInformation(
                    "Forwarded merge update {ForwardedUpdateCount} for {RowKey} with {FieldCount} fields.",
                    forwardedUpdateCount,
                    rowKey,
                    delta.Count);
            }
        }
        else if (suppressedUpdateCount <= 5 || suppressedUpdateCount % 1_000 == 0)
        {
            logger.LogInformation(
                "Suppressed merge update {SuppressedUpdateCount} for {RowKey}: listenerAttached={ListenerAttached}, itemSubscribed={ItemSubscribed}.",
                suppressedUpdateCount,
                rowKey,
                _listener is not null,
                _subscribedItems.Contains(rowKey));
        }

        return Task.FromResult(true);
    }

    public void ResetData()
    {
        lock (_sync)
        {
            _rows.Clear();
            _forwardedUpdateCount = 0;
            _suppressedUpdateCount = 0;
        }
    }

    private void SendSnapshot(string itemName)
    {
        if (_listener is null)
        {
            logger.LogWarning("Merge snapshot publish skipped for {ItemName}: no listener attached yet.", itemName);
            return;
        }

        // Copied under the lock instead of taking the cached dictionary reference and using it
        // after releasing the lock - IngestAsync mutates that same instance in place, and
        // serializing it concurrently on another thread (inside _listener.Update) could corrupt
        // the payload or throw a "collection modified" error.
        Dictionary<string, string?>? row;
        lock (_sync)
        {
            if (!_rows.TryGetValue(itemName, out var existing))
            {
                logger.LogWarning("Merge snapshot requested for unknown item {ItemName}; no row data found.", itemName);
                return;
            }

            row = new Dictionary<string, string?>(existing, StringComparer.OrdinalIgnoreCase);
        }

        logger.LogInformation("Sending merge snapshot for {ItemName}.", itemName);
        _listener.Update(itemName, row, isSnapshot: true);
        _listener.EndOfSnapshot(itemName);
    }
}
