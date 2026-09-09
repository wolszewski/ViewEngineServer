using System.Collections;
using Lightstreamer.Interfaces.Data;
using Microsoft.Extensions.Logging;

namespace Lightstreamer.DataProvider.Services;

public sealed class TradeCommandProvider(ILogger<TradeCommandProvider> logger) : IDataProvider
{
    public const string ListItemName = "TRADES_ALL";
    private readonly Lock _sync = new();
    private readonly HashSet<string> _allKeys = new(StringComparer.OrdinalIgnoreCase);
    private IItemEventListener? _listener;
    private bool _listSubscribed;
    private bool _snapshotPending;
    private long _sentCommandCount;
    private long _suppressedCommandCount;
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

    public void Init(IDictionary parameters, string configFile) { }

    public void SetListener(IItemEventListener eventListener)
    {
        _listener = eventListener;
        logger.LogInformation("Trade command adapter listener attached.");
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

        logger.LogInformation("Trade command adapter subscribed item {ItemName}.", itemName);
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

        logger.LogInformation("Trade command adapter unsubscribed item {ItemName}.", itemName);
        if (notify)
        {
            ListUnsubscribed?.Invoke();
        }
    }

    public void NotifyKeyAdded(string key)
    {
        bool isNew;
        lock (_sync)
        {
            isNew = _allKeys.Add(key);
        }

        if (isNew)
        {
            if (IsListSubscribed())
            {
                Send(key, DataProviderConstants.ADD_COMMAND, isSnapshot: false);
            }
            else
            {
                LogSuppressed(key);
            }
        }
    }

    public void NotifyKeyRemoved(string key)
    {
        bool wasPresent;
        lock (_sync)
        {
            wasPresent = _allKeys.Remove(key);
        }

        if (wasPresent)
        {
            if (IsListSubscribed())
            {
                Send(key, DataProviderConstants.DELETE_COMMAND, isSnapshot: false);
            }
            else
            {
                LogSuppressed(key);
            }
        }
    }

    public void ResetKeys()
    {
        lock (_sync)
        {
            _allKeys.Clear();
            _sentCommandCount = 0;
            _suppressedCommandCount = 0;
        }
    }

    public void PublishSnapshotAndEnableLiveUpdates()
    {
        if (_listener is null)
        {
            logger.LogWarning("Command snapshot publish skipped: no listener attached yet.");
            return;
        }

        List<string> keys;
        lock (_sync)
        {
            if (!_listSubscribed)
            {
                logger.LogInformation("Command snapshot publish skipped: item {ItemName} not subscribed.", ListItemName);
                _snapshotPending = false;
                return;
            }

            keys = [.. _allKeys];
            // _snapshotPending stays true until every key below has been sent - NotifyKeyAdded/
            // NotifyKeyRemoved gate on it, keeping a concurrently-running generator from
            // interleaving an ADD/DELETE for a not-yet-announced key ahead of this snapshot burst.
        }

        logger.LogInformation("Sending command snapshot with {KeyCount} keys.", keys.Count);
        foreach (var key in keys)
        {
            Send(key, DataProviderConstants.ADD_COMMAND, isSnapshot: true);
        }

        lock (_sync)
        {
            _snapshotPending = false;
        }

        _listener.EndOfSnapshot(ListItemName);
    }

    private void Send(string key, string command, bool isSnapshot)
    {
        long sentCommandCount;
        lock (_sync)
        {
            _sentCommandCount++;
            sentCommandCount = _sentCommandCount;
        }

        _listener?.Update(ListItemName, new Dictionary<string, string?>
        {
            { DataProviderConstants.KEY_FIELD, key },
            { DataProviderConstants.COMMAND_FIELD, command }
        }, isSnapshot);

        if (!isSnapshot && (sentCommandCount <= 5 || sentCommandCount % 1_000 == 0))
        {
            logger.LogInformation(
                "Forwarded command {Command} {SentCommandCount} for {RowKey}.",
                command,
                sentCommandCount,
                key);
        }
    }

    private void LogSuppressed(string key)
    {
        long suppressedCommandCount;
        bool listSubscribed;
        bool snapshotPending;
        lock (_sync)
        {
            _suppressedCommandCount++;
            suppressedCommandCount = _suppressedCommandCount;
            listSubscribed = _listSubscribed;
            snapshotPending = _snapshotPending;
        }

        if (suppressedCommandCount <= 5 || suppressedCommandCount % 1_000 == 0)
        {
            logger.LogInformation(
                "Suppressed command update {SuppressedCommandCount} for {RowKey}: listenerAttached={ListenerAttached}, listSubscribed={ListSubscribed}, snapshotPending={SnapshotPending}.",
                suppressedCommandCount,
                key,
                _listener is not null,
                listSubscribed,
                snapshotPending);
        }
    }

    private bool IsListSubscribed()
    {
        lock (_sync)
        {
            return _listSubscribed && !_snapshotPending;
        }
    }
}
