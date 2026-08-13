using System.Globalization;
using LiveViewEngine.HttpClient;

namespace LiveViewEngine.Poc.DataProvider.Services;

public sealed class TradeGeneratorService(
    LiveViewEngineHttpClient httpClient,
    ILogger<TradeGeneratorService> logger)
{
    private const string CollectionName = "trades";
    private const string CreatedDateFieldName = "createdDate";
    private const string UpdatedDateFieldName = "updatedDate";
    private readonly List<TradeFieldDefinition> _fieldDefinitions = CreateFieldDefinitions();
    private readonly Lock _sync = new();
    private CancellationTokenSource? _activeRun;
    private TradeGenerationStatus _status = TradeGenerationStatus.Idle();

    public IReadOnlyList<string> UpdatableFieldNames =>
        _fieldDefinitions.Where(static f => f.IsUserUpdatable).Select(static f => f.Name).ToList();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> UpdatableFieldNamesByType =>
        _fieldDefinitions
            .Where(static f => f.IsUserUpdatable)
            .GroupBy(static f => f.Type)
            .ToDictionary(
                static g => g.Key,
                static g => (IReadOnlyList<string>)g.Select(static f => f.Name).ToList());

    public TradeGenerationStatus Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    public event Action? StateChanged;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _activeRun is not null;
            }
        }
    }

    public async Task StartGenerationAsync(TradeGenerationSettings settings, CancellationToken ct = default)
    {
        CancellationTokenSource? newRun;
        lock (_sync)
        {
            if (_activeRun is not null)
            {
                return;
            }

            newRun = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeRun = newRun;
        }

        UpdateStatus(status =>
        {
            status.IsRunning = true;
            status.StatusMessage = "Preparing trade stream";
            status.LastError = null;
        });

        try
        {
            await RunGenerationAsync(settings, newRun.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Trade generation failed.");
            UpdateStatus(status =>
            {
                status.IsRunning = false;
                status.StatusMessage = "Generation failed";
                status.LastError = ex.Message;
            });
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeRun, newRun))
                {
                    _activeRun = null;
                }
            }

            newRun.Dispose();
            UpdateStatus(status =>
            {
                status.IsRunning = false;
                status.StatusMessage = "Idle";
                status.IsInUpdateMode = false;
            });
        }
    }

    public void StopGeneration()
    {
        CancellationTokenSource? active;
        lock (_sync)
        {
            active = _activeRun;
        }

        active?.Cancel();
    }

    private async Task RunGenerationAsync(TradeGenerationSettings settings, CancellationToken ct)
    {
        var fieldNames = _fieldDefinitions.Select(static field => field.Name).ToList();
        UpdateStatus(status =>
        {
            status.StatusMessage = "Creating collection";
            status.InitialTradeCount = settings.InitialTradeCount;
            status.UpdateFieldCount = settings.UpdateFieldCount;
            status.UpdateFrequencyHz = settings.UpdateFrequencyHz;
        });

        if (!await httpClient.CreateCollectionAsync(CollectionName, fieldNames, ct))
        {
            logger.LogWarning("Could not create collection '{CollectionName}'. Continuing with the assumption that it already exists.", CollectionName);
        }

        var trades = new List<TradeEntity>();
        for (int index = 0; index < settings.InitialTradeCount; index++)
        {
            ct.ThrowIfCancellationRequested();
            var trade = new TradeEntity(index + 1, CreateFieldValues(index + 1));
            trades.Add(trade);

            var success = await httpClient.IngestAsync(CollectionName, trade.Key, trade.Fields, ct);
            if (!success)
            {
                logger.LogWarning("Initial ingestion failed for trade {TradeId}.", trade.Id);
            }

            if (index % 500 == 0)
            {
                UpdateStatus(status =>
                {
                    status.TradesGenerated = index + 1;
                    status.StatusMessage = $"Ingesting initial trades ({index + 1}/{settings.InitialTradeCount})";
                });
            }
        }

        UpdateStatus(status =>
        {
            status.TradesGenerated = trades.Count;
            status.StatusMessage = "Transitioning to update mode";
            status.IsInUpdateMode = true;
        });

        var nextTradeIndex = 0;
        var updateDelay = TimeSpan.FromSeconds(1d / settings.UpdateFrequencyHz);
        while (!ct.IsCancellationRequested)
        {
            if (trades.Count == 0)
            {
                await Task.Delay(updateDelay, ct);
                continue;
            }

            var trade = trades[nextTradeIndex];
            nextTradeIndex = (nextTradeIndex + 1) % trades.Count;

            var changedFields = ApplyUpdates(trade, settings);
            var success = await httpClient.IngestAsync(CollectionName, trade.Key, changedFields, ct);
            if (!success)
            {
                logger.LogWarning("Update ingestion failed for trade {TradeId}.", trade.Id);
            }

            UpdateStatus(status =>
            {
                status.UpdatesSent++;
                status.LastUpdatedUtc = DateTimeOffset.UtcNow;
            });

            await Task.Delay(updateDelay, ct);
        }
    }

    private Dictionary<string, string?> ApplyUpdates(TradeEntity trade, TradeGenerationSettings settings)
    {
        var updatableFields = _fieldDefinitions.Where(static field => field.IsUserUpdatable).ToList();
        if (settings.UpdatableFields is { Count: > 0 })
        {
            var allowedSet = new HashSet<string>(settings.UpdatableFields, StringComparer.OrdinalIgnoreCase);
            updatableFields = updatableFields.Where(f => allowedSet.Contains(f.Name)).ToList();
        }

        var fieldsToUpdate = updatableFields.Count == 0
            ? 0
            : Random.Shared.Next(1, Math.Min(settings.UpdateFieldCount, updatableFields.Count) + 1);
        var selected = updatableFields.OrderBy(_ => Random.Shared.Next()).Take(fieldsToUpdate).ToList();
        var changedFields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in selected)
        {
            var value = field.UpdateValueFactory(trade);
            trade.Fields[field.Name] = value;
            changedFields[field.Name] = value;
        }

        var updatedDate = CreateTimestamp();
        trade.Fields[UpdatedDateFieldName] = updatedDate;
        changedFields[UpdatedDateFieldName] = updatedDate;

        return changedFields;
    }

    private Dictionary<string, string?> CreateFieldValues(int tradeId)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in _fieldDefinitions)
        {
            values[field.Name] = field.InitialValueFactory(new TradeEntity(tradeId, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)));
        }

        var createdDate = CreateTimestamp();
        values[CreatedDateFieldName] = createdDate;
        values[UpdatedDateFieldName] = createdDate;

        return values;
    }

    private void UpdateStatus(Action<TradeGenerationStatus> mutator)
    {
        lock (_sync)
        {
            mutator(_status);
            _status.LastUpdatedUtc = DateTimeOffset.UtcNow;
        }

        StateChanged?.Invoke();
    }

    private static List<TradeFieldDefinition> CreateFieldDefinitions()
    {
        var definitions = new List<TradeFieldDefinition>();
        definitions.Add(new TradeFieldDefinition("tradeId", "string", trade => $"T{trade.Id:D6}", trade => $"T{trade.Id:D6}"));
        definitions.Add(new TradeFieldDefinition(CreatedDateFieldName, "datetime", _ => CreateTimestamp(), trade => GetFieldOrDefault(trade.Fields, CreatedDateFieldName, CreateTimestamp()), isUserUpdatable: false));
        definitions.Add(new TradeFieldDefinition(UpdatedDateFieldName, "datetime", _ => CreateTimestamp(), _ => CreateTimestamp(), isUserUpdatable: false));
        definitions.Add(new TradeFieldDefinition("accountId", "int", trade => ((trade.Id * 13) % 100000).ToString(), trade => ((trade.Id * 13 + 17) % 100000).ToString()));
        definitions.Add(new TradeFieldDefinition("quantity", "int", trade => ((trade.Id % 500) + 1).ToString(), trade => ((int.Parse(trade.Fields["quantity"] ?? "1") + 1) % 1000 + 1).ToString()));
        definitions.Add(new TradeFieldDefinition("price", "decimal", trade => ((trade.Id % 1000) / 100m + 100).ToString("0.00", CultureInfo.InvariantCulture), trade => (decimal.Parse(trade.Fields["price"] ?? "100.00", CultureInfo.InvariantCulture) + 0.01m).ToString("0.00", CultureInfo.InvariantCulture)));
        definitions.Add(new TradeFieldDefinition("side", "enum", trade => (trade.Id % 3) switch { 0 => "Buy", 1 => "Sell", _ => "Hold" }, trade => (trade.Id % 3) switch { 0 => "Sell", 1 => "Hold", _ => "Buy" }));
        definitions.Add(new TradeFieldDefinition("status", "enum", trade => (trade.Id % 4) switch { 0 => "New", 1 => "Working", 2 => "Filled", _ => "Cancelled" }, trade => (trade.Id % 4) switch { 0 => "Working", 1 => "Filled", 2 => "Cancelled", _ => "New" }));
        definitions.Add(new TradeFieldDefinition("notional", "decimal", trade => (trade.Id * 15m).ToString("0.00", CultureInfo.InvariantCulture), trade => (decimal.Parse(trade.Fields["notional"] ?? "0.00", CultureInfo.InvariantCulture) + 50m).ToString("0.00", CultureInfo.InvariantCulture)));

        for (int index = 0; index < 30; index++)
        {
            var fieldIndex = index;
            definitions.Add(new TradeFieldDefinition(
                $"stringField{fieldIndex:D2}",
                "string",
                trade => $"{fieldIndex:D2}-{trade.Id % 1000:D3}",
                trade => $"{fieldIndex:D2}-{(trade.Id + fieldIndex) % 1000:D3}"));
        }

        for (int index = 0; index < 23; index++)
        {
            var fieldIndex = index;
            var fieldName = $"intField{fieldIndex:D2}";
            definitions.Add(new TradeFieldDefinition(
                fieldName,
                "int",
                trade => ((trade.Id * (fieldIndex + 3)) % 100000).ToString(),
                trade => ((int.Parse(GetFieldOrDefault(trade.Fields, fieldName, "0")) + (fieldIndex + 1)) % 100000).ToString()));
        }

        for (int index = 0; index < 20; index++)
        {
            var fieldIndex = index;
            var fieldName = $"decimalField{fieldIndex:D2}";
            definitions.Add(new TradeFieldDefinition(
                fieldName,
                "decimal",
                trade => ((trade.Id % 1000) * 0.25m + fieldIndex).ToString("0.00", CultureInfo.InvariantCulture),
                trade => (decimal.Parse(GetFieldOrDefault(trade.Fields, fieldName, "0.00"), CultureInfo.InvariantCulture)
                    + 0.05m + fieldIndex / 100m).ToString("0.00", CultureInfo.InvariantCulture)));
        }

        for (int index = 0; index < 20; index++)
        {
            var fieldIndex = index;
            definitions.Add(new TradeFieldDefinition(
                $"enumField{fieldIndex:D2}",
                "enum",
                trade => ((trade.Id + fieldIndex) % 4) switch
                {
                    0 => "Low",
                    1 => "Medium",
                    2 => "High",
                    _ => "Critical"
                },
                trade => ((trade.Id + fieldIndex) % 4) switch
                {
                    0 => "Medium",
                    1 => "High",
                    2 => "Critical",
                    _ => "Low"
                }));
        }

        return definitions;
    }

    private static string GetFieldOrDefault(
        IReadOnlyDictionary<string, string?> fields,
        string fieldName,
        string defaultValue)
    {
        if (fields.TryGetValue(fieldName, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return defaultValue;
    }

    private static string CreateTimestamp()
    {
        return DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }

    private sealed class TradeFieldDefinition(
        string name,
        string type,
        Func<TradeEntity, string> initialValueFactory,
        Func<TradeEntity, string> updateValueFactory,
        bool isUserUpdatable = true)
    {
        public string Name { get; } = name;
        public string Type { get; } = type;
        public Func<TradeEntity, string> InitialValueFactory { get; } = initialValueFactory;
        public Func<TradeEntity, string> UpdateValueFactory { get; } = updateValueFactory;
        public bool IsUserUpdatable { get; } = isUserUpdatable;
    }

    private sealed record TradeEntity(int Id, Dictionary<string, string?> Fields)
    {
        public string Key => $"trade-{Id}";
    }
}
