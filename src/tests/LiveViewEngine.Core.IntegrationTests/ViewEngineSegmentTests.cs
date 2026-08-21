using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Output;
using LiveViewEngine.Core.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveViewEngine.Core.IntegrationTests;

public class ViewEngineSegmentTests
{
    private static (ViewEngine engine, CapturingPublisher publisher) CreateEngine(LiveViewEngineOptions? options = null)
    {
        var metrics = new ViewEngineMetrics();
        var store = new CollectionStore(metrics, options ?? new LiveViewEngineOptions { EagerIndexing = false });
        var publisher = new CapturingPublisher();
        var engine = new ViewEngine(store, publisher, NullLogger<ViewEngine>.Instance, metrics);
        return (engine, publisher);
    }

    private static CollectionSchema TradesSchema() =>
        new("trades", ["instrument", "status", "amount", "valueDate"],
            [ScalarFieldType.String, ScalarFieldType.String, ScalarFieldType.Decimal, ScalarFieldType.DateOnly]);

    private static async Task SetupTradesAsync(ViewEngine engine)
    {
        await engine.IngestAsync(new CreateCollectionCommand
        {
            CollectionId = "trades",
            Schema = TradesSchema()
        });
    }

    private static Task<IngestResult> UpsertTrade(ViewEngine engine, string key, string instrument,
        string status, string amount, string valueDate) =>
        engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "trades",
            Key = key,
            Fields = new Dictionary<string, string?>
            {
                ["instrument"] = instrument,
                ["status"] = status,
                ["amount"] = amount,
                ["valueDate"] = valueDate
            }
        });

    private static async Task<SnapshotDelta> SubscribeAndGetSnapshot(
        ViewEngine engine, int connectionId, ViewDefinition view)
    {
        var events = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = connectionId,
            SubscriptionId = 1,
            View = view,
            StartIndex = 0
        });
        return Assert.IsType<SnapshotDelta>(Assert.Single(events));
    }

    [Fact]
    public async Task SubscribeToUnknownSegment_ReturnsEmpty()
    {
        var (engine, _) = CreateEngine();
        await SetupTradesAsync(engine);
        await UpsertTrade(engine, "t1", "AAPL", "open", "1000", "2024-01-15");

        var events = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            View = new ViewDefinition { CollectionId = "trades", SegmentId = "nonexistent" },
            StartIndex = 0
        });

        Assert.Empty(events);
    }

    [Fact]
    public async Task CreateSegment_DuplicateId_Fails()
    {
        var (engine, _) = CreateEngine();
        await SetupTradesAsync(engine);

        var r1 = await engine.IngestAsync(new CreateSegmentCommand
        {
            CollectionId = "trades",
            SegmentId = "today",
            Filters = [new FilterSpec("valueDate", FilterOperator.Eq, "2024-01-15")]
        });
        var r2 = await engine.IngestAsync(new CreateSegmentCommand
        {
            CollectionId = "trades",
            SegmentId = "today",
            Filters = [new FilterSpec("valueDate", FilterOperator.Eq, "2024-01-16")]
        });

        Assert.True(r1.Success);
        Assert.False(r2.Success);
    }

    [Fact]
    public async Task CreateSegment_UnknownCollection_Fails()
    {
        var (engine, _) = CreateEngine();

        var result = await engine.IngestAsync(new CreateSegmentCommand
        {
            CollectionId = "nonexistent",
            SegmentId = "today",
            Filters = [new FilterSpec("valueDate", FilterOperator.Eq, "2024-01-15")]
        });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task SubscribeToSegment_ReturnsOnlyMatchingRows()
    {
        var (engine, _) = CreateEngine();
        await SetupTradesAsync(engine);
        await engine.IngestAsync(new CreateSegmentCommand
        {
            CollectionId = "trades",
            SegmentId = "today",
            Filters = [new FilterSpec("valueDate", FilterOperator.Eq, "2024-01-15")]
        });

        await UpsertTrade(engine, "t1", "AAPL", "open", "1000", "2024-01-15");
        await UpsertTrade(engine, "t2", "MSFT", "open", "2000", "2024-01-16");
        await UpsertTrade(engine, "t3", "GOOG", "open", "3000", "2024-01-15");

        var snap = await SubscribeAndGetSnapshot(engine, 1,
            new ViewDefinition { CollectionId = "trades", SegmentId = "today" });

        Assert.Equal(2, snap.TotalCount);
        var keyIdx = snap.Schema.GetFieldIndex("key");
        var ids = snap.Rows.Select(r => r[keyIdx]).ToHashSet();
        Assert.Contains("t1", ids);
        Assert.Contains("t3", ids);
        Assert.DoesNotContain("t2", ids);
    }

    [Fact]
    public async Task SubscribeToSegment_WithAdditionalUserFilter_AppliesBothFilters()
    {
        var (engine, _) = CreateEngine();
        await SetupTradesAsync(engine);
        await engine.IngestAsync(new CreateSegmentCommand
        {
            CollectionId = "trades",
            SegmentId = "today",
            Filters = [new FilterSpec("valueDate", FilterOperator.Eq, "2024-01-15")]
        });

        await UpsertTrade(engine, "t1", "AAPL", "open", "1000", "2024-01-15");
        await UpsertTrade(engine, "t2", "MSFT", "closed", "2000", "2024-01-15");
        await UpsertTrade(engine, "t3", "GOOG", "open", "3000", "2024-01-15");
        await UpsertTrade(engine, "t4", "AAPL", "open", "500", "2024-01-16");

        var snap = await SubscribeAndGetSnapshot(engine, 1,
            new ViewDefinition
            {
                CollectionId = "trades",
                SegmentId = "today",
                Filters = [new FilterSpec("status", FilterOperator.Eq, "open")]
            });

        Assert.Equal(2, snap.TotalCount);
        var keyIdx = snap.Schema.GetFieldIndex("key");
        var ids = snap.Rows.Select(r => r[keyIdx]).ToHashSet();
        Assert.Contains("t1", ids);
        Assert.Contains("t3", ids);
    }

    [Fact]
    public async Task SubscribeToSegment_WithSorting_RowsAreSorted()
    {
        var (engine, _) = CreateEngine();
        await SetupTradesAsync(engine);
        await engine.IngestAsync(new CreateSegmentCommand
        {
            CollectionId = "trades",
            SegmentId = "today",
            Filters = [new FilterSpec("valueDate", FilterOperator.Eq, "2024-01-15")]
        });

        await UpsertTrade(engine, "t1", "MSFT", "open", "500", "2024-01-15");
        await UpsertTrade(engine, "t2", "AAPL", "open", "1500", "2024-01-15");
        await UpsertTrade(engine, "t3", "GOOG", "open", "1000", "2024-01-15");

        var snap = await SubscribeAndGetSnapshot(engine, 1,
            new ViewDefinition
            {
                CollectionId = "trades",
                SegmentId = "today",
                SortColumn = "instrument",
                SortAscending = true
            });

        Assert.Equal(3, snap.TotalCount);
        var instrIdx = snap.Schema.GetFieldIndex("instrument");
        var instruments = snap.Rows.Select(r => r[instrIdx]).ToList();
        Assert.Equal(["AAPL", "GOOG", "MSFT"], instruments);
    }

    [Fact]
    public async Task SubscribeToSegment_ReceivesMutationDeltas_WhenRowEntersSegment()
    {
        var (engine, publisher) = CreateEngine();
        await SetupTradesAsync(engine);
        await engine.IngestAsync(new CreateSegmentCommand
        {
            CollectionId = "trades",
            SegmentId = "today",
            Filters = [new FilterSpec("valueDate", FilterOperator.Eq, "2024-01-15")]
        });

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            View = new ViewDefinition { CollectionId = "trades", SegmentId = "today" },
            StartIndex = 0,
            PageSize = 50
        });

        var countBefore = publisher.Published.Count;

        // Insert a row that belongs to the segment.
        await UpsertTrade(engine, "t1", "AAPL", "open", "1000", "2024-01-15");

        Assert.True(publisher.Published.Count > countBefore);
    }

    [Fact]
    public async Task SubscribeToSegment_DoesNotReceiveMutationDeltas_WhenRowOutsideSegment()
    {
        var (engine, publisher) = CreateEngine();
        await SetupTradesAsync(engine);
        await engine.IngestAsync(new CreateSegmentCommand
        {
            CollectionId = "trades",
            SegmentId = "today",
            Filters = [new FilterSpec("valueDate", FilterOperator.Eq, "2024-01-15")]
        });

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            View = new ViewDefinition { CollectionId = "trades", SegmentId = "today" },
            StartIndex = 0,
            PageSize = 50
        });

        var countBefore = publisher.Published.Count;

        // Insert a row that does NOT belong to the segment.
        await UpsertTrade(engine, "t2", "MSFT", "open", "2000", "2024-01-16");

        Assert.Equal(countBefore, publisher.Published.Count);
    }

    [Fact]
    public async Task TwoSegments_SameCollection_IndependentViews()
    {
        var (engine, _) = CreateEngine();
        await SetupTradesAsync(engine);

        await engine.IngestAsync(new CreateSegmentCommand
        {
            CollectionId = "trades",
            SegmentId = "today",
            Filters = [new FilterSpec("valueDate", FilterOperator.Eq, "2024-01-15")]
        });
        await engine.IngestAsync(new CreateSegmentCommand
        {
            CollectionId = "trades",
            SegmentId = "tomorrow",
            Filters = [new FilterSpec("valueDate", FilterOperator.Eq, "2024-01-16")]
        });

        await UpsertTrade(engine, "t1", "AAPL", "open", "1000", "2024-01-15");
        await UpsertTrade(engine, "t2", "MSFT", "open", "2000", "2024-01-16");
        await UpsertTrade(engine, "t3", "GOOG", "open", "3000", "2024-01-17");

        var todaySnap = await SubscribeAndGetSnapshot(engine, 1,
            new ViewDefinition { CollectionId = "trades", SegmentId = "today" });
        var tomorrowSnap = await SubscribeAndGetSnapshot(engine, 2,
            new ViewDefinition { CollectionId = "trades", SegmentId = "tomorrow" });

        Assert.Equal(1, todaySnap.TotalCount);
        Assert.Equal(1, tomorrowSnap.TotalCount);

        var keyIdx = todaySnap.Schema.GetFieldIndex("key");
        Assert.Equal("t1", todaySnap.Rows[0][keyIdx]);
        Assert.Equal("t2", tomorrowSnap.Rows[0][keyIdx]);
    }

    [Fact]
    public async Task SubscribeWithoutSegmentId_StillReceivesAllRows()
    {
        var (engine, _) = CreateEngine();
        await SetupTradesAsync(engine);
        await engine.IngestAsync(new CreateSegmentCommand
        {
            CollectionId = "trades",
            SegmentId = "today",
            Filters = [new FilterSpec("valueDate", FilterOperator.Eq, "2024-01-15")]
        });

        await UpsertTrade(engine, "t1", "AAPL", "open", "1000", "2024-01-15");
        await UpsertTrade(engine, "t2", "MSFT", "open", "2000", "2024-01-16");

        var snap = await SubscribeAndGetSnapshot(engine, 1,
            new ViewDefinition { CollectionId = "trades" });

        Assert.Equal(2, snap.TotalCount);
    }

    [Fact]
    public async Task SubscribeToSegment_AfterPrePopulation_ReturnsOnlyMatchingRows()
    {
        var (engine, _) = CreateEngine(new LiveViewEngineOptions
        {
            EagerIndexing = false
        });
        await SetupTradesAsync(engine);
        await engine.IngestAsync(new CreateSegmentCommand
        {
            CollectionId = "trades",
            SegmentId = "today",
            Filters = [new FilterSpec("valueDate", FilterOperator.Eq, "2024-01-15")]
        });

        await UpsertTrade(engine, "t1", "AAPL", "open", "1000", "2024-01-15");
        await UpsertTrade(engine, "t2", "MSFT", "open", "2000", "2024-01-16");
        await UpsertTrade(engine, "t3", "GOOG", "open", "3000", "2024-01-15");

        var snap = await SubscribeAndGetSnapshot(engine, 1,
            new ViewDefinition { CollectionId = "trades", SegmentId = "today" });

        Assert.Equal(2, snap.TotalCount);
        var keyIdx = snap.Schema.GetFieldIndex("key");
        var ids = snap.Rows.Select(r => r[keyIdx]).ToHashSet();
        Assert.Contains("t1", ids);
        Assert.Contains("t3", ids);
        Assert.DoesNotContain("t2", ids);
    }
}
