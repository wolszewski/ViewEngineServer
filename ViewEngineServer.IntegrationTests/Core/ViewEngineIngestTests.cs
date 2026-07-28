using Microsoft.Extensions.Logging.Abstractions;
using ViewEngineServer.Core;

namespace ViewEngineServer.IntegrationTests.Engine;

public class ViewEngineIngestTests
{

    private static (ViewEngine engine, CapturingPublisher publisher, ICollectionStore store)
        CreateEngine()
    {
        var store = new CollectionStore();
        var publisher = new CapturingPublisher();
        var logger = NullLogger<ViewEngine>.Instance;
        var engine = new ViewEngine(store, publisher, logger);
        return (engine, publisher, store);
    }

    private static CollectionSchema OrdersSchema() =>
        new()
        {
            CollectionId = "orders",
            Fields =
            [
                new FieldDefinition("id", FieldType.String, IsPrimaryKey: true),
                new FieldDefinition("customer", FieldType.String, IsSortable: true, IsFilterable: true),
                new FieldDefinition("amount", FieldType.Double, IsSortable: true),
                new FieldDefinition("status", FieldType.String, IsFilterable: true)
            ]
        };

    private static Task<IngestResult> CreateOrders(ViewEngine engine) =>
        engine.IngestAsync(new CreateCollectionCommand
        {
            CollectionId = "orders",
            Schema = OrdersSchema()
        });

    private static Task<IngestResult> Upsert(ViewEngine engine, string id, string customer,
                                             double amount, string status = "open") =>
        engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "orders",
            Fields = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["customer"] = customer,
                ["amount"] = amount,
                ["status"] = status
            }
        });


    [Fact]
    public async Task CreateCollection_Succeeds()
    {
        var (engine, _, store) = CreateEngine();
        var result = await CreateOrders(engine);

        Assert.True(result.Success);
        Assert.Contains("orders", store.CollectionIds);
    }

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
    public async Task IngestThenSubscribe_SnapshotContainsAllRows()
    {
        var (engine, _, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", 100);
        await Upsert(engine, "o2", "Bob", 200);
        await Upsert(engine, "o3", "Carol", 150);

        var events = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client1",
            View = new ViewDefinition { CollectionId = "orders" },
            StartIndex = 0,
            PageSize = 50
        });

        var snapshot = Assert.IsType<SnapshotEvent>(events.Single());
        Assert.Equal(3, snapshot.TotalCount);
        Assert.Equal(3, snapshot.Rows.Count);

        var ids = snapshot.Rows.Select(r => r["id"]?.ToString()).ToHashSet();
        Assert.Contains("o1", ids);
        Assert.Contains("o2", ids);
        Assert.Contains("o3", ids);
    }

    [Fact]
    public async Task IngestThenSubscribe_SnapshotValuesMatchIngested()
    {
        var (engine, _, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", 99.5, "closed");

        var events = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client1",
            View = new ViewDefinition { CollectionId = "orders" },
            StartIndex = 0,
            PageSize = 10
        });

        var snapshot = Assert.IsType<SnapshotEvent>(events.Single());
        var row = snapshot.Rows.Single();
        Assert.Equal("Alice", row["customer"]);
        Assert.Equal(99.5, row["amount"]);
        Assert.Equal("closed", row["status"]);
    }


    [Fact]
    public async Task SubscribeThenIngest_NewRow_PublishesInsertOrSnapshot()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateOrders(engine);

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client1",
            View = new ViewDefinition { CollectionId = "orders" },
            StartIndex = 0,
            PageSize = 10
        });

        await Upsert(engine, "o1", "Alice", 100);

        var deltas = publisher.EventsFor("client1").ToList();
        Assert.NotEmpty(deltas);
        Assert.Contains(deltas, e => e is RowInsertEvent or SnapshotEvent);
    }

    [Fact]
    public async Task SubscribeThenUpsertExisting_PublishesUpdateEvent()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", 100);

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
            Fields = new Dictionary<string, object?> { ["id"] = "o1", ["amount"] = 250.0 }
        });

        var updateEvents = publisher.EventsFor("client1").OfType<RowUpdateEvent>().ToList();
        Assert.NotEmpty(updateEvents);
        var evt = updateEvents.First();
        Assert.Equal("o1", evt.RowId);
        Assert.True(evt.ChangedFields.ContainsKey("amount"));
        Assert.Equal(250.0, evt.ChangedFields["amount"]);
    }

    [Fact]
    public async Task SubscribeThenDelete_PublishesRemoveEvent()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", 100);

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
            PrimaryKeyValue = "o1"
        });

        var removeEvents = publisher.EventsFor("client1").OfType<RowRemoveEvent>().ToList();
        Assert.NotEmpty(removeEvents);
    }


    [Fact]
    public async Task UpsertToMissingCollection_Fails()
    {
        var (engine, _, _) = CreateEngine();
        var result = await Upsert(engine, "o1", "Alice", 100);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteFromMissingCollection_Fails()
    {
        var (engine, _, _) = CreateEngine();
        var result = await engine.IngestAsync(new DeleteRowCommand
        {
            CollectionId = "missing",
            PrimaryKeyValue = "o1"
        });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task DeleteNonExistentRow_IsIdempotent()
    {
        var (engine, _, _) = CreateEngine();
        await CreateOrders(engine);

        var result = await engine.IngestAsync(new DeleteRowCommand
        {
            CollectionId = "orders",
            PrimaryKeyValue = "ghost"
        });

        Assert.True(result.Success);
    }


    [Fact]
    public async Task Subscribe_SortedByAmount_SnapshotIsOrdered()
    {
        var (engine, _, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o3", "Carol", 300);
        await Upsert(engine, "o1", "Alice", 100);
        await Upsert(engine, "o2", "Bob", 200);

        var events = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client1",
            View = new ViewDefinition
            {
                CollectionId = "orders",
                SortColumn = "amount",
                SortAscending = true
            },
            StartIndex = 0,
            PageSize = 10
        });

        var snapshot = Assert.IsType<SnapshotEvent>(events.Single());
        var amounts = snapshot.Rows.Select(r => (double)r["amount"]!).ToList();
        Assert.Equal([100, 200, 300], amounts);
    }

    [Fact]
    public async Task Subscribe_SortedDescending_SnapshotIsReverseOrdered()
    {
        var (engine, _, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", 100);
        await Upsert(engine, "o2", "Bob", 200);

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
        var amounts = snapshot.Rows.Select(r => (double)r["amount"]!).ToList();
        Assert.Equal([200, 100], amounts);
    }


    [Fact]
    public async Task Subscribe_WithFilter_SnapshotOnlyContainsMatchingRows()
    {
        var (engine, _, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", 100, "open");
        await Upsert(engine, "o2", "Bob", 200, "closed");
        await Upsert(engine, "o3", "Carol", 150, "open");

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
        Assert.All(snapshot.Rows, r => Assert.Equal("open", r["status"]));
    }


    [Fact]
    public async Task Subscribe_PageSize_LimitsReturnedRows()
    {
        var (engine, _, _) = CreateEngine();
        await CreateOrders(engine);
        for (int i = 1; i <= 10; i++)
            await Upsert(engine, $"o{i}", $"Customer{i}", i * 10);

        var events = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client1",
            View = new ViewDefinition { CollectionId = "orders" },
            StartIndex = 0,
            PageSize = 3
        });

        var snapshot = Assert.IsType<SnapshotEvent>(events.Single());
        Assert.Equal(10, snapshot.TotalCount);
        Assert.Equal(3, snapshot.Rows.Count);
    }

    [Fact]
    public async Task ChangeViewport_ReturnsNewPage()
    {
        var (engine, _, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", 100);
        await Upsert(engine, "o2", "Bob", 200);
        await Upsert(engine, "o3", "Carol", 300);

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client1",
            View = new ViewDefinition { CollectionId = "orders", SortColumn = "amount", SortAscending = true },
            StartIndex = 0,
            PageSize = 2
        });

        var events = await engine.SubscribeAsync(new ChangeViewportCommand
        {
            ConnectionId = "client1",
            StartIndex = 2,
            PageSize = 2
        });

        var snapshot = Assert.IsType<SnapshotEvent>(events.Single());
        Assert.Equal(2, snapshot.StartIndex);
        Assert.Single(snapshot.Rows);
    }


    [Fact]
    public async Task Unsubscribe_ThenIngest_DoesNotPublishToDisconnectedClient()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", 100);

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client1",
            View = new ViewDefinition { CollectionId = "orders" },
            StartIndex = 0,
            PageSize = 10
        });

        await engine.SubscribeAsync(new UnsubscribeCommand { ConnectionId = "client1" });

        var countBefore = publisher.EventsFor("client1").Count();
        await Upsert(engine, "o2", "Bob", 200);
        var countAfter = publisher.EventsFor("client1").Count();

        Assert.Equal(countBefore, countAfter);
    }


    [Fact]
    public async Task SubscribeToMissingCollection_ReturnsNoEvents()
    {
        var (engine, _, _) = CreateEngine();

        var events = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client1",
            View = new ViewDefinition { CollectionId = "missing" },
            StartIndex = 0,
            PageSize = 10
        });

        Assert.Empty(events);
    }


    [Fact]
    public async Task MultipleSubscribers_SameView_BothReceiveDelta()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", 100);

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "clientA",
            View = new ViewDefinition { CollectionId = "orders" },
            StartIndex = 0,
            PageSize = 10
        });
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "clientB",
            View = new ViewDefinition { CollectionId = "orders" },
            StartIndex = 0,
            PageSize = 10
        });

        await Upsert(engine, "o2", "Bob", 200);

        Assert.NotEmpty(publisher.EventsFor("clientA"));
        Assert.NotEmpty(publisher.EventsFor("clientB"));
    }
}
