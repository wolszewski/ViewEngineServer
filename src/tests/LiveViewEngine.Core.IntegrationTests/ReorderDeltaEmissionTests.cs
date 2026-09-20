using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.DataIngest;
using LiveViewEngine.Core.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveViewEngine.Core.IntegrationTests;

// 2026-09-20 AR-01: ComputePositionDeltas appended a RowUpdateDelta whenever the mutated row landed
// inside the viewport, repeating a subset of the row the positional delta had just carried. These
// pin one delta per reorder for each branch that can set newIn, and that the row payload carries the
// post-mutation values that made the update redundant.
public class ReorderDeltaEmissionTests
{
    private const string CollectionId = "trades";
    private const int ConnectionId = 1;

    // Six rows sorted by "sortVal" ascending occupy positions 0-5; the subscription below sees only
    // positions 2 and 3, so a row can start inside, above or below the viewport.
    private static readonly (string Key, string SortValue)[] SeedRows =
        [("r0", "a"), ("r1", "b"), ("r2", "c"), ("r3", "d"), ("r4", "e"), ("r5", "f")];

    private const int ViewportStart = 2;
    private const int ViewportPageSize = 2;

    private static async Task SeedAndSubscribe(ViewEngine engine)
    {
        foreach (var (key, sortValue) in SeedRows)
        {
            Assert.True((await engine.IngestAsync(new UpsertRowCommand
            {
                CollectionId = CollectionId,
                Key = key,
                Fields = new Dictionary<string, string?> { ["id"] = key, ["sortVal"] = sortValue }
            })).Success);
        }

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = ConnectionId,
            View = new ViewDefinition { CollectionId = CollectionId, SortColumn = "sortVal", SortAscending = true },
            StartIndex = ViewportStart,
            PageSize = ViewportPageSize
        });
    }

    private static async Task<(ViewEngine Engine, CapturingPublisher Publisher)> CreateEngine()
    {
        var metrics = new ViewEngineMetrics();
        var store = new CollectionStore(metrics, new LiveViewEngineOptions { EagerIndexing = false });
        var publisher = new CapturingPublisher();
        var engine = new ViewEngine(store, publisher, NullLogger<ViewEngine>.Instance, metrics);

        Assert.True((await engine.IngestAsync(new CreateCollectionCommand
        {
            CollectionId = CollectionId,
            Schema = new CollectionSchema(CollectionId, ["id", "sortVal"])
        })).Success);

        await SeedAndSubscribe(engine);
        return (engine, publisher);
    }

    private static Task<IngestResult> MoveTo(ViewEngine engine, string key, string sortValue) =>
        engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = CollectionId,
            Key = key,
            Fields = new Dictionary<string, string?> { ["sortVal"] = sortValue }
        });

    // oldIn && newIn: r2 slides from position 2 to position 3, both inside the viewport.
    [Fact]
    public async Task MoveWithinViewport_EmitsOnlyTheReplace()
    {
        var (engine, publisher) = await CreateEngine();

        Assert.True((await MoveTo(engine, "r2", "dd")).Success);

        var replace = Assert.IsType<RowReplaceEvent>(Assert.Single(publisher.EventsFor(ConnectionId)));
        Assert.Equal("r2", replace.RemovedRowId);
        Assert.Equal(0, replace.RemovePosition);
        Assert.Equal(1, replace.InsertPosition);
        Assert.Equal("dd", replace.Row["sortVal"]);
    }

    // oldBefore && newIn: r0 starts above the viewport and lands inside it.
    [Fact]
    public async Task MoveIntoViewportFromAbove_EmitsOnlyTheReplace()
    {
        var (engine, publisher) = await CreateEngine();

        Assert.True((await MoveTo(engine, "r0", "dd")).Success);

        var replace = Assert.IsType<RowReplaceEvent>(Assert.Single(publisher.EventsFor(ConnectionId)));
        Assert.Equal(1, replace.InsertPosition);
        Assert.Equal("r0", replace.Row["id"]);
        Assert.Equal("dd", replace.Row["sortVal"]);
    }

    // !oldIn && !oldBefore && newIn: r5 starts below the viewport and lands inside it.
    [Fact]
    public async Task MoveIntoViewportFromBelow_EmitsOnlyTheReplace()
    {
        var (engine, publisher) = await CreateEngine();

        Assert.True((await MoveTo(engine, "r5", "bb")).Success);

        var replace = Assert.IsType<RowReplaceEvent>(Assert.Single(publisher.EventsFor(ConnectionId)));
        Assert.Equal(0, replace.InsertPosition);
        Assert.Equal("r5", replace.Row["id"]);
        Assert.Equal("bb", replace.Row["sortVal"]);
    }

    // The position-unchanged path still needs its update: nothing else carries the new value.
    [Fact]
    public async Task SortValueChangeThatKeepsPosition_StillEmitsTheUpdate()
    {
        var (engine, publisher) = await CreateEngine();

        // "cc" still sorts between "b" and "d", so r2 stays at position 2.
        Assert.True((await MoveTo(engine, "r2", "cc")).Success);

        var update = Assert.IsType<RowUpdateEvent>(Assert.Single(publisher.EventsFor(ConnectionId)));
        Assert.Equal("r2", update.RowId);
        Assert.Equal(0, update.Position);
        Assert.Equal("cc", update.ChangedFields["sortVal"]);
    }

    // A non-sort field change takes the fast path, which was never affected by AR-01.
    [Fact]
    public async Task NonSortFieldChange_StillEmitsTheUpdate()
    {
        var (engine, publisher) = await CreateEngine();

        Assert.True((await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = CollectionId,
            Key = "r2",
            Fields = new Dictionary<string, string?> { ["id"] = "r2-renamed" }
        })).Success);

        var update = Assert.IsType<RowUpdateEvent>(Assert.Single(publisher.EventsFor(ConnectionId)));
        Assert.Equal("r2", update.RowId);
        Assert.Equal("r2-renamed", update.ChangedFields["id"]);
    }
}
