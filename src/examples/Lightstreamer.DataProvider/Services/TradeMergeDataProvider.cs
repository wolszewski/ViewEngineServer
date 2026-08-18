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
        Dictionary<string, string?> snapshot;
        bool isNew;
        bool shouldForward;
        long forwardedUpdateCount = 0;
        lock (_sync)
        {
            isNew = !_rows.ContainsKey(rowKey);
            snapshot = new Dictionary<string, string?>(fieldValues, StringComparer.OrdinalIgnoreCase);
            _rows[rowKey] = snapshot;
            shouldForward = _listener is not null && _subscribedItems.Contains(rowKey);
            if (shouldForward)
            {
                _forwardedUpdateCount++;
                forwardedUpdateCount = _forwardedUpdateCount;
            }
        }

        if (isNew)
        {
            commandProvider.NotifyKeyAdded(rowKey);
        }

        if (shouldForward && _listener is not null)
        {
            _listener.Update(rowKey, snapshot, isSnapshot: false);

            if (forwardedUpdateCount <= 5 || forwardedUpdateCount % 1_000 == 0)
            {
                logger.LogInformation(
                    "Forwarded merge update {ForwardedUpdateCount} for {RowKey} with {FieldCount} fields.",
                    forwardedUpdateCount,
                    rowKey,
                    snapshot.Count);
            }
        }

        return Task.FromResult(true);
    }

    public void ResetData()
    {
        lock (_sync)
        {
            _rows.Clear();
            _forwardedUpdateCount = 0;
        }
    }

    private void SendSnapshot(string itemName)
    {
        if (_listener is null)
        {
            return;
        }

        Dictionary<string, string?>? row;
        lock (_sync)
        {
            if (!_rows.TryGetValue(itemName, out row))
            {
                return;
            }
        }

        logger.LogInformation("Sending merge snapshot for {ItemName}.", itemName);
        _listener.Update(itemName, row, isSnapshot: true);
        _listener.EndOfSnapshot(itemName);
    }
}
