using System.Collections;
using Lightstreamer.Interfaces.Data;
using LiveViewEngine.Poc.Shared;

namespace Lightstreamer.DataProvider.Blazor.Services;

public sealed class TradeDataProvider : IDataProvider, ITradeIngestionClient
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Dictionary<string, string?>> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _subscribedItems = new(StringComparer.OrdinalIgnoreCase);
    private IItemEventListener? _listener;

    public void Init(IDictionary parameters, string configFile)
    {
    }

    public void SetListener(IItemEventListener eventListener)
    {
        _listener = eventListener;
    }

    public bool IsSnapshotAvailable(string itemName) => true;

    public void Subscribe(string itemName)
    {
        lock (_sync)
        {
            _subscribedItems.Add(itemName);
        }

        SendSnapshot(itemName);
    }

    public void Unsubscribe(string itemName)
    {
        lock (_sync)
        {
            _subscribedItems.Remove(itemName);
        }
    }

    public Task<bool> CreateCollectionAsync(
        string collectionName,
        IReadOnlyList<string> fieldNames,
        IReadOnlyList<string>? fieldTypes = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> IngestAsync(string collectionName, string rowKey, IReadOnlyDictionary<string, string?> fieldValues, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string?> snapshot;
        lock (_sync)
        {
            snapshot = new Dictionary<string, string?>(fieldValues, StringComparer.OrdinalIgnoreCase);
            _rows[rowKey] = snapshot;
        }

        if (_listener is not null)
        {
            lock (_sync)
            {
                if (_subscribedItems.Contains(rowKey))
                {
                    _listener.Update(rowKey, snapshot, isSnapshot: false);
                }
            }
        }

        return Task.FromResult(true);
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

        _listener.Update(itemName, row, isSnapshot: true);
        _listener.EndOfSnapshot(itemName);
    }
}
