using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveViewEngine.Core.IntegrationTests;

public class CompactSnapshotStreamingTests
{
    private static (ViewEngine engine, CapturingPublisher publisher) CreateEngine()
    {
        var metrics = new ViewEngineMetrics();
        var store = new CollectionStore(metrics, new LiveViewEngineOptions { EagerIndexing = false });
        var publisher = new CapturingPublisher();
        return (new ViewEngine(store, publisher, NullLogger<ViewEngine>.Instance, metrics), publisher);
    }

    [Fact]
    public async Task Subscribe_StreamsSnapshot_UsesSchemaOrderForRequestedFields()
    {
        var (engine, _) = CreateEngine();
        await engine.IngestAsync(new CreateCollectionCommand
        {
            CollectionId = "orders",
            Schema = new CollectionSchema("orders", ["status", "amount", "customer"])
        });
        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "orders",
            Key = "o1",
            Fields = new Dictionary<string, string?>
            {
                ["customer"] = "Alice",
                ["amount"] = "100",
                ["status"] = "open"
            }
        });

        var events = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            SubscriptionId = 7,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                Fields = ["amount", "customer"]
            },
            StartIndex = 0,
            PageSize = 10
        });

        var start = Assert.IsType<SnapshotStartDelta>(events[0]);
        Assert.Equal([0, 2, 3], start.VisibleFieldIndexes);

        var rows = Assert.IsType<SnapshotRowsDelta>(events[1]);
        Assert.Collection(rows.Rows.Single(),
            value => Assert.Equal("o1", value),
            value => Assert.Equal("100", value),
            value => Assert.Equal("Alice", value));

        Assert.IsType<EndOfSnapshotDelta>(events[^1]);
    }

    [Fact]
    public async Task Subscribe_StreamsSnapshot_BatchesLargePageAndEndsWithEos()
    {
        var (engine, _) = CreateEngine();
        await engine.IngestAsync(new CreateCollectionCommand
        {
            CollectionId = "orders",
            Schema = new CollectionSchema("orders", ["amount"])
        });

        for (int i = 0; i < 300; i++)
        {
            await engine.IngestAsync(new UpsertRowCommand
            {
                CollectionId = "orders",
                Key = $"o{i:D3}",
                Fields = new Dictionary<string, string?> { ["amount"] = i.ToString() }
            });
        }

        var events = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            SubscriptionId = 7,
            View = new ViewDefinition { CollectionId = "orders" },
            StartIndex = 0,
            PageSize = 300
        });

        Assert.IsType<SnapshotStartDelta>(events[0]);
        var batches = events.OfType<SnapshotRowsDelta>().ToArray();
        Assert.Equal([128, 128, 44], batches.Select(b => b.Rows.Count).ToArray());
        Assert.IsType<EndOfSnapshotDelta>(events[^1]);
    }
}
