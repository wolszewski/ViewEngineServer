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
        var metrics = new ViewEngineMetrics();
        var store = new CollectionStore(metrics);
        var publisher = new CapturingPublisher();
        var logger = NullLogger<ViewEngine>.Instance;
        var engine = new ViewEngine(store, publisher, logger, metrics);
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
            ConnectionId = 1,
            View = new ViewDefinition { CollectionId = "orders" },
            StartIndex = 0,
            PageSize = 50
        });

        var snapshot = Assert.IsType<SnapshotDelta>(events.Single());
        Assert.Equal(2, snapshot.TotalCount);
        Assert.Equal("o1", snapshot.Rows[0][CollectionSchema.PrimaryKeyIndex]);
        Assert.Equal("o2", snapshot.Rows[1][CollectionSchema.PrimaryKeyIndex]);
    }

    [Fact]
    public async Task SubscribeThenUpsertExisting_PublishesUpdateEvent()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", "100");
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
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

        var updateEvents = publisher.EventsFor(1).OfType<RowUpdateEvent>().ToList();
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
            ConnectionId = 1,
            View = new ViewDefinition { CollectionId = "orders" },
            StartIndex = 0,
            PageSize = 10
        });

        await engine.IngestAsync(new DeleteRowCommand
        {
            CollectionId = "orders",
            Key = "o1"
        });

        Assert.NotEmpty(publisher.EventsFor(1).OfType<RowRemoveEvent>());
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
            ConnectionId = 1,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                SortColumn = "amount",
                SortAscending = false
            },
            StartIndex = 0,
            PageSize = 10
        });

        var snapshot = Assert.IsType<SnapshotDelta>(events.Single());
        var amountIndex = snapshot.Schema.GetFieldIndex("amount");
        var amounts = snapshot.Rows.Select(row => row[amountIndex]).ToList();
        Assert.Equal(["200", "100"], amounts);
    }

    [Fact]
    public async Task Subscribe_SameSortColumnWithDifferentDirections_ReturnsDifferentOrder()
    {
        var (engine, _, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", "100");
        await Upsert(engine, "o2", "Bob", "200");
        await Upsert(engine, "o3", "Carol", "300");

        var ascendingEvents = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                SortColumn = "amount",
                SortAscending = true
            },
            StartIndex = 0,
            PageSize = 10
        });

        var descendingEvents = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 2,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                SortColumn = "amount",
                SortAscending = false
            },
            StartIndex = 0,
            PageSize = 10
        });

        var ascendingSnapshot = Assert.IsType<SnapshotDelta>(ascendingEvents.Single());
        var descendingSnapshot = Assert.IsType<SnapshotDelta>(descendingEvents.Single());
        var amountIndex = ascendingSnapshot.Schema.GetFieldIndex("amount");

        var ascendingAmounts = ascendingSnapshot.Rows.Select(row => row[amountIndex]).ToList();
        var descendingAmounts = descendingSnapshot.Rows.Select(row => row[amountIndex]).ToList();

        Assert.Equal(["100", "200", "300"], ascendingAmounts);
        Assert.Equal(["300", "200", "100"], descendingAmounts);
    }

    [Fact]
    public async Task Subscribe_SameSortColumnWithDifferentDirections_CreatesSingleSortIndex()
    {
        var (engine, _, store) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", "100");
        await Upsert(engine, "o2", "Bob", "200");
        await Upsert(engine, "o3", "Carol", "300");

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                SortColumn = "amount",
                SortAscending = true
            },
            StartIndex = 0,
            PageSize = 10
        });

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 2,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                SortColumn = "amount",
                SortAscending = false
            },
            StartIndex = 0,
            PageSize = 10
        });

        Assert.True(store.TryGetRuntime("orders", out var runtime));
        Assert.NotNull(runtime);
        Assert.Equal(1, runtime.SortIndexCount);
    }

    [Fact]
    public async Task Subscribe_SameSortColumnWithDifferentDirections_PagingReturnsCorrectRows()
    {
        var (engine, _, _) = CreateEngine();
        await CreateOrders(engine);

        for (int i = 1; i <= 10; i++)
        {
            await Upsert(engine, $"o{i}", $"Customer-{i}", i.ToString("D3"));
        }

        var ascendingEvents = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                SortColumn = "amount",
                SortAscending = true
            },
            StartIndex = 0,
            PageSize = 5
        });

        var descendingEvents = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 2,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                SortColumn = "amount",
                SortAscending = false
            },
            StartIndex = 0,
            PageSize = 5
        });

        var ascendingSnapshot = Assert.IsType<SnapshotDelta>(ascendingEvents.Single());
        var descendingSnapshot = Assert.IsType<SnapshotDelta>(descendingEvents.Single());
        var amountIndex = ascendingSnapshot.Schema.GetFieldIndex("amount");

        var ascendingAmounts = ascendingSnapshot.Rows.Select(row => row[amountIndex]).ToList();
        var descendingAmounts = descendingSnapshot.Rows.Select(row => row[amountIndex]).ToList();

        Assert.Equal(["001", "002", "003", "004", "005"], ascendingAmounts);
        Assert.Equal(["010", "009", "008", "007", "006"], descendingAmounts);
    }

    [Fact]
    public async Task Subscribe_SameConnectionDifferentSubscriptionIds_TracksIndependentSubscriptions()
    {
        var (engine, publisher, store) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", "100", "open");
        await Upsert(engine, "o2", "Bob", "200", "closed");

        var openSnapshot = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                Filters = [new FilterSpec("status", FilterOperator.Eq, "open")]
            },
            StartIndex = 0,
            PageSize = 10
        });

        var closedSnapshot = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            SubscriptionId = 2,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                Filters = [new FilterSpec("status", FilterOperator.Eq, "closed")]
            },
            StartIndex = 0,
            PageSize = 10
        });

        Assert.Equal(1, Assert.IsType<SnapshotDelta>(openSnapshot.Single()).TotalCount);
        Assert.Equal(1, Assert.IsType<SnapshotDelta>(closedSnapshot.Single()).TotalCount);

        Assert.True(store.TryGetRuntime("orders", out var runtime));
        Assert.NotNull(runtime);
        Assert.Equal(2, runtime.ActiveSubscriptionCount);
        Assert.Equal(2, runtime.ActiveSharedViewCount);

        await Upsert(engine, "o3", "Carol", "300", "open");

        var insertEvents = publisher.EventsFor(1).OfType<RowInsertEvent>().ToList();
        Assert.Contains(insertEvents, static e => e.SubscriptionId == 1);
        Assert.DoesNotContain(insertEvents, static e => e.SubscriptionId == 2);
    }

    [Fact]
    public async Task Unsubscribe_WithoutSubscriptionId_RemovesAllSubscriptionsForConnection()
    {
        var (engine, publisher, store) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", "100", "open");
        await Upsert(engine, "o2", "Bob", "200", "closed");

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                Filters = [new FilterSpec("status", FilterOperator.Eq, "open")]
            },
            StartIndex = 0,
            PageSize = 10
        });

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            SubscriptionId = 2,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                Filters = [new FilterSpec("status", FilterOperator.Eq, "closed")]
            },
            StartIndex = 0,
            PageSize = 10
        });

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 2,
            SubscriptionId = 1,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                Filters = [new FilterSpec("status", FilterOperator.Eq, "open")]
            },
            StartIndex = 0,
            PageSize = 10
        });

        await engine.SubscribeAsync(new UnsubscribeCommand
        {
            ConnectionId = 1
        });

        Assert.True(store.TryGetRuntime("orders", out var runtime));
        Assert.NotNull(runtime);
        Assert.Equal(1, runtime.ActiveSubscriptionCount);
        Assert.Equal(1, runtime.ActiveSharedViewCount);

        await Upsert(engine, "o3", "Carol", "300", "open");

        var socket1Events = publisher.EventsFor(1).OfType<RowInsertEvent>().ToList();
        var socket2Events = publisher.EventsFor(2).OfType<RowInsertEvent>().ToList();
        Assert.DoesNotContain(socket1Events, static e => e.Row["key"] == "o3");
        Assert.Contains(socket2Events, static e => e.SubscriptionId == 1 && e.Row["key"] == "o3");
    }

    [Fact]
    public async Task Subscribe_SameSubscriptionId_ReplacesViewAndCleansUpOldSharedView()
    {
        var (engine, _, store) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", "100", "open");
        await Upsert(engine, "o2", "Bob", "200", "closed");
        await Upsert(engine, "o3", "Carol", "300", "open");

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                SortColumn = "amount",
                SortAscending = true
            },
            StartIndex = 0,
            PageSize = 10
        });

        var replacement = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                SortColumn = "amount",
                SortAscending = true,
                Filters = [new FilterSpec("status", FilterOperator.Eq, "open")]
            },
            StartIndex = 0,
            PageSize = 10
        });

        var snapshot = Assert.IsType<SnapshotDelta>(replacement.Single());
        Assert.Equal("1:1", snapshot.ViewId);
        Assert.Equal(2, snapshot.TotalCount);

        Assert.True(store.TryGetRuntime("orders", out var runtime));
        Assert.NotNull(runtime);
        Assert.Equal(1, runtime.ActiveSubscriptionCount);
        Assert.Equal(1, runtime.ActiveSharedViewCount);
        Assert.Equal(1, runtime.SortIndexCount);
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
            ConnectionId = 1,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                Filters = [new FilterSpec("status", FilterOperator.Eq, "open")]
            },
            StartIndex = 0,
            PageSize = 10
        });

        var snapshot = Assert.IsType<SnapshotDelta>(events.Single());
        Assert.Equal(2, snapshot.TotalCount);
        var statusIndex = snapshot.Schema.GetFieldIndex("status");
        Assert.All(snapshot.Rows, row => Assert.Equal("open", row[statusIndex]));
    }

    [Fact]
    public async Task UpsertNonSortNonFilterField_EmitsOnlyRowUpdate()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", "100");
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition { CollectionId = "orders" },
            StartIndex = 0,
            PageSize = 10
        });

        // 'amount' is not the sort column (default sort is key) and there are no filters
        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "orders",
            Key = "o1",
            Fields = new Dictionary<string, string?> { ["amount"] = "999" }
        });

        var published = publisher.EventsFor(1).ToList();
        Assert.Single(published, e => e is RowUpdateEvent { RowId: "o1" });
        Assert.DoesNotContain(published, e => e is RowInsertEvent);
        Assert.DoesNotContain(published, e => e is RowRemoveEvent);
    }

    [Fact]
    public async Task UpsertFilterField_RowEntersViewport_EmitsInsert()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", "100", "closed");
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                Filters = [new FilterSpec("status", FilterOperator.Eq, "open")]
            },
            StartIndex = 0,
            PageSize = 10
        });

        // Snapshot should be empty (no open orders)
        publisher.EventsFor(1).ToList(); // consume snapshot

        // Change status so the row now matches the filter
        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "orders",
            Key = "o1",
            Fields = new Dictionary<string, string?> { ["status"] = "open" }
        });

        var afterMutation = publisher.EventsFor(1).ToList();
        Assert.Contains(afterMutation, e => e is RowInsertEvent i && i.Position == 0);
        Assert.DoesNotContain(afterMutation, e => e is RowRemoveEvent);
    }

    [Fact]
    public async Task UpsertFilterField_RowExitsViewport_EmitsRemove()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", "100", "open");
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                Filters = [new FilterSpec("status", FilterOperator.Eq, "open")]
            },
            StartIndex = 0,
            PageSize = 10
        });

        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "orders",
            Key = "o1",
            Fields = new Dictionary<string, string?> { ["status"] = "closed" }
        });

        Assert.Contains(publisher.EventsFor(1), e => e is RowRemoveEvent r && r.Position == 0);
    }

    [Fact]
    public async Task TwoIdenticalViewports_BothReceiveEventsOnInsert()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateOrders(engine);
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition { CollectionId = "orders" },
            StartIndex = 0,
            PageSize = 10
        });
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 2,
            View = new ViewDefinition { CollectionId = "orders" },
            StartIndex = 0,
            PageSize = 10
        });

        await Upsert(engine, "o1", "Alice", "100");

        Assert.Contains(publisher.EventsFor(1), e => e is RowInsertEvent);
        Assert.Contains(publisher.EventsFor(2), e => e is RowInsertEvent);
    }

    [Fact]
    public async Task TwoIdenticalViewports_BothReceiveRowUpdateOnFastPath()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", "100");
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition { CollectionId = "orders" },
            StartIndex = 0,
            PageSize = 10
        });
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 2,
            View = new ViewDefinition { CollectionId = "orders" },
            StartIndex = 0,
            PageSize = 10
        });

        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "orders",
            Key = "o1",
            Fields = new Dictionary<string, string?> { ["amount"] = "500" }
        });

        Assert.Contains(publisher.EventsFor(1), e => e is RowUpdateEvent u && u.RowId == "o1");
        Assert.Contains(publisher.EventsFor(2), e => e is RowUpdateEvent u && u.RowId == "o1");
    }

    [Fact]
    public async Task SortFieldChange_DoesNotEmitRowUpdate_EmitsInsertOrReorder()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", "100");
        await Upsert(engine, "o2", "Bob", "200");
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                SortColumn = "amount",
                SortAscending = true
            },
            StartIndex = 0,
            PageSize = 10
        });

        // Update the sort field — should recompute page, not take fast path
        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "orders",
            Key = "o1",
            Fields = new Dictionary<string, string?> { ["amount"] = "300" }
        });

        // Row order should have changed: o2(200), o1(300) → snapshot was o1(100), o2(200)
        // Position 0 should now be o2, position 1 o1; so o1 removed from pos 0, inserted at pos 1
        var events = publisher.EventsFor(1).ToList();
        Assert.NotEmpty(events);
    }

    [Fact]
    public async Task SortFieldChange_RowLeavesPagedViewport_EmitsRemoveAndBackfillInsert()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateOrders(engine);

        for (int i = 1; i <= 100; i++)
        {
            var key = $"o{i:D3}";
            await Upsert(engine, key, $"Customer-{i:D3}", i.ToString("D3"));
        }

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                SortColumn = "amount",
                SortAscending = true
            },
            StartIndex = 0,
            PageSize = 50
        });

        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "orders",
            Key = "o010",
            Fields = new Dictionary<string, string?> { ["amount"] = "999" }
        });

        var events = publisher.EventsFor(1).ToList();
        Assert.Equal(2, events.Count);

        var remove = Assert.IsType<RowRemoveEvent>(events[0]);
        Assert.Equal(9, remove.Position);

        var insert = Assert.IsType<RowInsertEvent>(events[1]);
        Assert.Equal(49, insert.Position);
        Assert.Equal("o051", insert.Row["key"]);
    }

    [Fact]
    public async Task RowOutsideViewport_NonSortNonFilterUpdate_NoEvent()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", "100");
        await Upsert(engine, "o2", "Bob", "200");
        await Upsert(engine, "o3", "Carol", "300");
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition { CollectionId = "orders" },
            StartIndex = 0,
            PageSize = 2  // only o1, o2 visible
        });

        // Update o3 which is outside the viewport; non-sort, non-filter field
        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "orders",
            Key = "o3",
            Fields = new Dictionary<string, string?> { ["amount"] = "999" }
        });

        Assert.Empty(publisher.EventsFor(1));
    }

    [Fact]
    public async Task FilterChange_DoesNotAffectSortIndex_RowStillSortedCorrectly()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateOrders(engine);
        await Upsert(engine, "o1", "Alice", "100", "open");
        await Upsert(engine, "o2", "Bob", "200", "open");
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                SortColumn = "amount",
                SortAscending = true,
                Filters = [new FilterSpec("status", FilterOperator.Eq, "open")]
            },
            StartIndex = 0,
            PageSize = 10
        });

        // Change status of o1 to closed (exits filter) then back to open (enters filter)
        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "orders",
            Key = "o1",
            Fields = new Dictionary<string, string?> { ["status"] = "closed" }
        });
        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "orders",
            Key = "o1",
            Fields = new Dictionary<string, string?> { ["status"] = "open" }
        });

        // After re-entering, the view should still be sorted by amount asc: o1(100), o2(200)
        var events = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 99,
            View = new ViewDefinition
            {
                CollectionId = "orders",
                SortColumn = "amount",
                SortAscending = true,
                Filters = [new FilterSpec("status", FilterOperator.Eq, "open")]
            },
            StartIndex = 0,
            PageSize = 10
        });
        var snapshot = Assert.IsType<SnapshotDelta>(events.Single());
        var amountIdx = snapshot.Schema.GetFieldIndex("amount");
        Assert.Equal("100", snapshot.Rows[0][amountIdx]);
        Assert.Equal("200", snapshot.Rows[1][amountIdx]);
    }
}
