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
    public event Action? ListSubscribed;
    public event Action? ListUnsubscribed;

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

        if (isNew && IsListSubscribed())
        {
            Send(key, DataProviderConstants.ADD_COMMAND, isSnapshot: false);
        }
    }

    public void NotifyKeyRemoved(string key)
    {
        bool wasPresent;
        lock (_sync)
        {
            wasPresent = _allKeys.Remove(key);
        }

        if (wasPresent && IsListSubscribed())
        {
            Send(key, DataProviderConstants.DELETE_COMMAND, isSnapshot: false);
        }
    }

    public void ResetKeys()
    {
        lock (_sync)
        {
            _allKeys.Clear();
        }
    }

    public void PublishSnapshotAndEnableLiveUpdates()
    {
        if (_listener is null)
        {
            return;
        }

        List<string> keys;
        bool isSubscribed;
        lock (_sync)
        {
            isSubscribed = _listSubscribed;
            if (!isSubscribed)
            {
                _snapshotPending = false;
                return;
            }

            keys = [.. _allKeys];
            _snapshotPending = false;
        }

        logger.LogInformation("Sending command snapshot with {KeyCount} keys.", keys.Count);
        foreach (var key in keys)
        {
            Send(key, DataProviderConstants.ADD_COMMAND, isSnapshot: true);
        }

        _listener.EndOfSnapshot(ListItemName);
    }

    private void Send(string key, string command, bool isSnapshot)
    {
        _listener?.Update(ListItemName, new Dictionary<string, string?>
        {
            { DataProviderConstants.KEY_FIELD, key },
            { DataProviderConstants.COMMAND_FIELD, command }
        }, isSnapshot);
    }

    private bool IsListSubscribed()
    {
        lock (_sync)
        {
            return _listSubscribed && !_snapshotPending;
        }
    }
}
