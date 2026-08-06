using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Output;
using LiveViewEngine.Core.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveViewEngine.Core.IntegrationTests;

public class ViewEngineIngestTests
{
    private static (ViewEngine engine, CapturingPublisher publisher, ICollectionStore store) CreateEngine()
    {
        var store = new CollectionStore();
        var publisher = new CapturingPublisher();
        var rowOutputFormatter = new JsonDictionaryRowOutputFormatter();
        var logger = NullLogger<ViewEngine>.Instance;
        var engine = new ViewEngine(store, publisher, rowOutputFormatter, logger);
        return (engine, publisher, store);
    }

    private static CollectionSchema OrdersSchema() => new("orders", ["customer", "amount", "status"]);

    private static Task<IngestResult> CreateOrders(ViewEngine engine) =>
        engine.IngestAsync(new CreateCollectionCommand
        {
            CollectionId = "orders",
            Schema = OrdersSchema()
        });

    private static Task<IngestResult> Upsert(
        ViewEngine engine,
        string key,
        string customer,
        string amount,
        string status = "open") =>
        engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "orders",
            Key = key,
            Fields = new Dictionary<string, string?>
            {
                ["customer"] = customer,
                ["amount"] = amount,
                ["status"] = status
            }
        });

    [Fact]
    public async Task CreateCollection_DuplicateId_Fails()
    {
        var (engine, _, _) = CreateEngine();
        await CreateOrders(engine);

        var result = await CreateOrders(engine);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task IngestUnknownCommand_Fails()
    {
        var (engine, _, _) = CreateEngine();
        var result = await engine.IngestAsync(new UnknownTestCommand { CollectionId = "x" });
        Assert.False(result.Success);
    }

    private sealed class UnknownTestCommand : IngestCommand { }

    [Fact]
    public async Task Subscribe_ReturnsSnapshotWithRows()
    {
        var (engine, _, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", "100");
        await Upsert(engine, "o2", "Bob", "200");

        var events = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client1",
            View = new ViewDefinition { CollectionId = "orders" },
            StartIndex = 0,
            PageSize = 50
        });

        var snapshot = Assert.IsType<SnapshotEvent>(events.Single());
        Assert.Equal(2, snapshot.TotalCount);
        Assert.Equal("o1", snapshot.Rows[0]["key"]);
        Assert.Equal("o2", snapshot.Rows[1]["key"]);
    }

    [Fact]
    public async Task SubscribeThenUpsertExisting_PublishesUpdateEvent()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", "100");
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client1",
            View = new ViewDefinition { CollectionId = "orders" },
            StartIndex = 0,
            PageSize = 10
        });

        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "orders",
            Key = "o1",
            Fields = new Dictionary<string, string?> { ["amount"] = "250" }
        });

        var updateEvents = publisher.EventsFor("client1").OfType<RowUpdateEvent>().ToList();
        Assert.NotEmpty(updateEvents);
        var evt = updateEvents.First();
        Assert.Equal("o1", evt.RowId);
        Assert.Equal("250", evt.ChangedFields["amount"]);
    }

    [Fact]
    public async Task SubscribeThenDelete_PublishesRemoveEvent()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", "100");
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client1",
            View = new ViewDefinition { CollectionId = "orders" },
            StartIndex = 0,
            PageSize = 10
        });

        await engine.IngestAsync(new DeleteRowCommand
        {
            CollectionId = "orders",
            Key = "o1"
        });

        Assert.NotEmpty(publisher.EventsFor("client1").OfType<RowRemoveEvent>());
    }

    [Fact]
    public async Task Subscribe_SortedDescending_ReturnsSortedRows()
    {
        var (engine, _, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", "100");
        await Upsert(engine, "o2", "Bob", "200");

        var events = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client1",
            View = new ViewDefinition
            {
                CollectionId = "orders",
                SortColumn = "amount",
                SortAscending = false
            },
            StartIndex = 0,
            PageSize = 10
        });

        var snapshot = Assert.IsType<SnapshotEvent>(events.Single());
        var amounts = snapshot.Rows.Select(r => r["amount"]).ToList();
        Assert.Equal(["200", "100"], amounts);
    }

    [Fact]
    public async Task Subscribe_WithFilter_ReturnsMatchingRows()
    {
        var (engine, _, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", "100", "open");
        await Upsert(engine, "o2", "Bob", "200", "closed");
        await Upsert(engine, "o3", "Carol", "300", "open");

        var events = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client1",
            View = new ViewDefinition
            {
                CollectionId = "orders",
                Filters = [new FilterSpec("status", FilterOperator.Eq, "open")]
            },
            StartIndex = 0,
            PageSize = 10
        });

        var snapshot = Assert.IsType<SnapshotEvent>(events.Single());
        Assert.Equal(2, snapshot.TotalCount);
        Assert.All(snapshot.Rows, row => Assert.Equal("open", row["status"]));
    }
}
