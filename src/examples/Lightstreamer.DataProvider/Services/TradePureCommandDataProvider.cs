using System.Collections;
using Lightstreamer.Interfaces.Data;
using LiveViewEngine.Poc.Shared;
using Microsoft.Extensions.Logging;

namespace Lightstreamer.DataProvider.Services;

// Pure COMMAND mode counterpart to TradeCommandProvider + TradeMergeDataProvider's two-level push:
// a single COMMAND-mode item carries every subscribed field directly on each ADD/UPDATE, so no
// second-level MERGE subscription per row is needed.
public sealed class TradePureCommandDataProvider(ILogger<TradePureCommandDataProvider> logger)
    : IDataProvider, ITradeIngestionClient
{
    public const string ListItemName = "TRADES_ALL_PURE";
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Dictionary<string, string?>> _rows = new(StringComparer.OrdinalIgnoreCase);
    private IItemEventListener? _listener;
    private bool _listSubscribed;
    private bool _snapshotPending;
    private long _forwardedUpdateCount;
    private long _suppressedUpdateCount;
    public event Action? ListSubscribed;
    public event Action? ListUnsubscribed;

    public bool IsSubscribed
    {
        get
        {
            lock (_sync)
            {
                return _listSubscribed;
            }
        }
    }

    public void Init(IDictionary parameters, string configFile)
    {
    }

    public void SetListener(IItemEventListener eventListener)
    {
        _listener = eventListener;
        logger.LogInformation("Trade pure-command adapter listener attached.");
    }

    public bool IsSnapshotAvailable(string itemName) => true;

    public void Subscribe(string itemName)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(itemName, ListItemName))
        {
            return;
        }

        bool notify;
        lock (_sync)
        {
            notify = !_listSubscribed;
            _listSubscribed = true;
            if (notify)
            {
                _snapshotPending = true;
            }
        }

        logger.LogInformation("Trade pure-command adapter subscribed item {ItemName}.", itemName);
        if (notify)
        {
            ListSubscribed?.Invoke();
        }
    }

    public void Unsubscribe(string itemName)
    {
        bool notify;
        lock (_sync)
        {
            notify = _listSubscribed;
            _listSubscribed = false;
            _snapshotPending = false;
        }

        logger.LogInformation("Trade pure-command adapter unsubscribed item {ItemName}.", itemName);
        if (notify)
        {
            ListUnsubscribed?.Invoke();
        }
    }

    public Task<bool> CreateCollectionAsync(
        string collectionName,
        IReadOnlyList<string> fieldNames,
        IReadOnlyList<string>? fieldTypes = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }

    public Task<bool> IngestAsync(
        string collectionName,
        string rowKey,
        IReadOnlyDictionary<string, string?> fieldValues,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, string?> snapshot;
        bool isNew;
        bool shouldSend;
        long forwardedUpdateCount = 0;
        long suppressedUpdateCount = 0;
        lock (_sync)
        {
            isNew = !_rows.TryGetValue(rowKey, out var existing);
            if (isNew)
            {
                snapshot = new Dictionary<string, string?>(fieldValues, StringComparer.OrdinalIgnoreCase);
                _rows[rowKey] = snapshot;
            }
            else
            {
                snapshot = existing!;
                foreach (var (field, value) in fieldValues)
                {
                    snapshot[field] = value;
                }
            }

            shouldSend = _listener is not null && _listSubscribed && !_snapshotPending;
            if (shouldSend)
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

        if (shouldSend)
        {
            var payload = new Dictionary<string, string?>(fieldValues, StringComparer.OrdinalIgnoreCase)
            {
                [DataProviderConstants.KEY_FIELD] = rowKey,
                [DataProviderConstants.COMMAND_FIELD] = isNew ? DataProviderConstants.ADD_COMMAND : DataProviderConstants.UPDATE_COMMAND
            };
            _listener!.Update(ListItemName, payload, isSnapshot: false);

            if (forwardedUpdateCount <= 5 || forwardedUpdateCount % 1_000 == 0)
            {
                logger.LogInformation(
                    "Forwarded pure-command {Command} {ForwardedUpdateCount} for {RowKey} with {FieldCount} fields.",
                    isNew ? DataProviderConstants.ADD_COMMAND : DataProviderConstants.UPDATE_COMMAND,
                    forwardedUpdateCount,
                    rowKey,
                    fieldValues.Count);
            }
        }
        else if (suppressedUpdateCount <= 5 || suppressedUpdateCount % 1_000 == 0)
        {
            logger.LogInformation(
                "Suppressed pure-command update {SuppressedUpdateCount} for {RowKey}: listenerAttached={ListenerAttached}, listSubscribed={ListSubscribed}, snapshotPending={SnapshotPending}.",
                suppressedUpdateCount,
                rowKey,
                _listener is not null,
                _listSubscribed,
                _snapshotPending);
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

    public void PublishSnapshotAndEnableLiveUpdates()
    {
        if (_listener is null)
        {
            logger.LogWarning("Pure-command snapshot publish skipped: no listener attached yet.");
            return;
        }

        List<KeyValuePair<string, Dictionary<string, string?>>> rows;
        lock (_sync)
        {
            if (!_listSubscribed)
            {
                logger.LogInformation("Pure-command snapshot publish skipped: item {ItemName} not subscribed.", ListItemName);
                _snapshotPending = false;
                return;
            }

            rows = [.. _rows];
            _snapshotPending = false;
        }

        logger.LogInformation("Sending pure-command snapshot with {RowCount} rows.", rows.Count);
        foreach (var (key, fields) in rows)
        {
            var payload = new Dictionary<string, string?>(fields, StringComparer.OrdinalIgnoreCase)
            {
                [DataProviderConstants.KEY_FIELD] = key,
                [DataProviderConstants.COMMAND_FIELD] = DataProviderConstants.ADD_COMMAND
            };
            _listener.Update(ListItemName, payload, isSnapshot: true);
        }

        _listener.EndOfSnapshot(ListItemName);
    }
}
