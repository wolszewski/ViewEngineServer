using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Output;
using LiveViewEngine.TcpProtocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ViewEngineServer.WebApp.Tcp;

namespace LiveViewEngine.Core.IntegrationTests;

public class TcpIngestRequestDispatcherTests
{
    [Fact]
    public async Task CreateCollection_ReturnsSchemaIncludingPrimaryKey()
    {
        var (dispatcher, _, _) = CreateDispatcher();

        var response = await dispatcher.DispatchAsync(
            new CreateCollectionRequestMessage(
                1,
                "trades",
                [
                    new TcpSchemaField(1, "tradeId", "string"),
                    new TcpSchemaField(2, "price", "decimal")
                ]),
            CancellationToken.None);

        var schema = Assert.IsType<SchemaResponseMessage>(response);
        Assert.Equal("trades", schema.CollectionName);
        Assert.Collection(
            schema.Fields,
            field =>
            {
                Assert.Equal(0, field.Index);
                Assert.Equal("key", field.Name);
            },
            field => Assert.Equal("tradeId", field.Name),
            field => Assert.Equal("price", field.Name));
    }

    [Fact]
    public async Task CreateCollection_WithBooleanField_ReturnsBooleanSchemaType()
    {
        var (dispatcher, _, _) = CreateDispatcher();

        var response = await dispatcher.DispatchAsync(
            new CreateCollectionRequestMessage(
                1,
                "flags",
                [
                    new TcpSchemaField(1, "active", "boolean")
                ]),
            CancellationToken.None);

        var schema = Assert.IsType<SchemaResponseMessage>(response);
        Assert.Collection(
            schema.Fields,
            field => Assert.Equal("string", field.Type),
            field => Assert.Equal("boolean", field.Type));
    }

    [Fact]
    public async Task Upsert_MapsIndexedFieldsToSchemaNames()
    {
        var (dispatcher, _, store) = CreateDispatcher(enableAsyncAcks: true);
        await dispatcher.DispatchAsync(
            new CreateCollectionRequestMessage(
                1,
                "trades",
                [
                    new TcpSchemaField(1, "tradeId", "string"),
                    new TcpSchemaField(2, "price", "decimal"),
                    new TcpSchemaField(3, "status", "string")
                ]),
            CancellationToken.None);

        var response = await dispatcher.DispatchAsync(
            new UpsertRequestMessage(
                2,
                "trades",
                "trade-1",
                [
                    new KeyValuePair<int, string?>(1, "T000001"),
                    new KeyValuePair<int, string?>(2, "101.25"),
                    new KeyValuePair<int, string?>(3, "Working")
                ]),
            CancellationToken.None);

        var ack = Assert.IsType<AckResponseMessage>(response);
        Assert.Equal(2, ack.RequestId);
        Assert.Equal("UPSERT", ack.Operation);
        var values = await WaitForRowAsync(store, "trades", "trade-1");
        Assert.Equal("trade-1", values[0]);
        Assert.Equal("T000001", values[1]);
        Assert.Equal("101.25", values[2]);
        Assert.Equal("Working", values[3]);
    }

    [Fact]
    public async Task Upsert_ReturnsNoAck_WhenAsyncAcksDisabled()
    {
        var (dispatcher, _, _) = CreateDispatcher(enableAsyncAcks: false);
        await dispatcher.DispatchAsync(
            new CreateCollectionRequestMessage(
                1,
                "trades",
                [
                    new TcpSchemaField(1, "tradeId", "string")
                ]),
            CancellationToken.None);

        var response = await dispatcher.DispatchAsync(
            new UpsertRequestMessage(
                2,
                "trades",
                "trade-1",
                [
                    new KeyValuePair<int, string?>(1, "T000001")
                ]),
            CancellationToken.None);

        Assert.Null(response);
    }

    private static async Task<string?[]> WaitForRowAsync(ICollectionStore store, string collectionId, string rowKey)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (store.TryGet(collectionId, out var collection) &&
                collection is not null &&
                collection.TryGetRowIndex(rowKey, out var rowIndex))
            {
                return collection.GetRowValues(rowIndex);
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for row '{rowKey}' in collection '{collectionId}'.");
    }

    private static (TcpIngestRequestDispatcher dispatcher, ViewEngine engine, ICollectionStore store) CreateDispatcher(
        bool enableAsyncAcks = true)
    {
        var metrics = new ViewEngineMetrics();
        var store = new CollectionStore(metrics, new LiveViewEngineOptions { EagerIndexing = false });
        var engine = new ViewEngine(store, new TestPublisher(), NullLogger<ViewEngine>.Instance, metrics);
        var dispatcher = new TcpIngestRequestDispatcher(
            engine,
            store,
            new TcpIngestOptions
            {
                CollectionQueueCapacity = 1024,
                EnableAsyncAcks = enableAsyncAcks
            },
            new TestHostApplicationLifetime(),
            NullLogger<TcpIngestRequestDispatcher>.Instance);
        return (dispatcher, engine, store);
    }

    private sealed class TestPublisher : IOutboundPublisher
    {
        public ValueTask PublishAsync(
            IReadOnlyList<SubscriberTarget> targets,
            IReadOnlyList<ViewDelta> deltas,
            CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication()
        {
        }
    }
}
