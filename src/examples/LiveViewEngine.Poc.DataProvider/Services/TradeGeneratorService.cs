using LiveViewEngine.Poc.Shared;
using LiveViewEngine.TcpClient;

namespace LiveViewEngine.Poc.DataProvider.Services;

public sealed class TradeGeneratorService : LiveViewEngine.Poc.Shared.TradeGeneratorService
{
    public TradeGeneratorService(ILiveViewEngineTcpClient tcpClient, ILogger<TradeGeneratorService> logger)
        : base(new LiveViewEngineTcpIngestionClient(tcpClient), logger)
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

    private sealed class LiveViewEngineTcpIngestionClient(ILiveViewEngineTcpClient tcpClient) : ITradeIngestionClient
    {
        public Task<bool> CreateCollectionAsync(
            string collectionName,
            IReadOnlyList<string> fieldNames,
            IReadOnlyList<string>? fieldTypes = null,
            CancellationToken cancellationToken = default)
            => tcpClient.CreateCollectionAsync(collectionName, fieldNames, fieldTypes, cancellationToken);

        public Task<bool> IngestAsync(string collectionName, string rowKey, IReadOnlyDictionary<string, string?> fieldValues, CancellationToken cancellationToken = default)
            => tcpClient.IngestAsync(collectionName, rowKey, fieldValues, cancellationToken);
    }
}
