using LiveViewEngine.HttpClient;
using LiveViewEngine.Poc.Shared;

namespace LiveViewEngine.Poc.DataProvider.Services;

public sealed class TradeGeneratorService : LiveViewEngine.Poc.Shared.TradeGeneratorService
{
    public TradeGeneratorService(LiveViewEngineHttpClient httpClient, ILogger<TradeGeneratorService> logger)
        : base(new LiveViewEngineHttpIngestionClient(httpClient), logger)
    {
    }

    public new TradeGenerationStatus Status
    {
        get
        {
            var status = base.Status;
            return new TradeGenerationStatus
            {
                IsRunning = status.IsRunning,
                IsInUpdateMode = status.IsInUpdateMode,
                InitialTradeCount = status.InitialTradeCount,
                UpdateFieldCount = status.UpdateFieldCount,
                UpdateFrequencyHz = status.UpdateFrequencyHz,
                TradesGenerated = status.TradesGenerated,
                UpdatesSent = status.UpdatesSent,
                UpdatesPerSecond = status.UpdatesPerSecond,
                StatusMessage = status.StatusMessage,
                LastError = status.LastError,
                LastUpdatedUtc = status.LastUpdatedUtc
            };
        }
    }

    private sealed class LiveViewEngineHttpIngestionClient(LiveViewEngineHttpClient httpClient) : ITradeIngestionClient
    {
        public Task<bool> CreateCollectionAsync(string collectionName, IReadOnlyList<string> fieldNames, CancellationToken cancellationToken = default)
            => httpClient.CreateCollectionAsync(collectionName, fieldNames, cancellationToken);

        public Task<bool> IngestAsync(string collectionName, string rowKey, IReadOnlyDictionary<string, string?> fieldValues, CancellationToken cancellationToken = default)
            => httpClient.IngestAsync(collectionName, rowKey, fieldValues, cancellationToken);
    }
}
