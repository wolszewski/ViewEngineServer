using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.DataIngest;
using LiveViewEngine.Core.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveViewEngine.Core.IntegrationTests;

// 2026-09-12 AR-08: FieldMask used a fixed 2-word (128 field) buffer, so a collection wider than
// that was accepted at create time and then threw IndexOutOfRangeException on the first write to,
// or projection of, a field past index 127.
public class WideSchemaTests
{
    private const string CollectionId = "wide";
    private const int FieldCount = 200;

    // Schema indexes are 1-based (CollectionSchema puts the key at index 0), so "f150" sits at
    // field index 150 — comfortably past the old 128 ceiling.
    private static readonly string[] FieldNames =
        [.. Enumerable.Range(1, FieldCount).Select(i => $"f{i:D3}")];

    private static (ViewEngine Engine, CapturingPublisher Publisher) CreateEngine()
    {
        var metrics = new ViewEngineMetrics();
        var store = new CollectionStore(metrics, new LiveViewEngineOptions { EagerIndexing = false });
        var publisher = new CapturingPublisher();
        var engine = new ViewEngine(store, publisher, NullLogger<ViewEngine>.Instance, metrics);
        return (engine, publisher);
    }

    private static Dictionary<string, string?> Row(string key, params (string Field, string Value)[] overrides)
    {
        var fields = new Dictionary<string, string?> { ["f001"] = key };
        foreach (var (field, value) in overrides)
        {
            fields[field] = value;
        }

        return fields;
    }

    private static Task<IngestResult> CreateCollection(ViewEngine engine) =>
        engine.IngestAsync(new CreateCollectionCommand
        {
            CollectionId = CollectionId,
            Schema = new CollectionSchema(CollectionId, FieldNames)
        });

    [Fact]
    public async Task UpsertRow_WritingFieldPastOldInlineLimit_Succeeds()
    {
        var (engine, _) = CreateEngine();
        Assert.True((await CreateCollection(engine)).Success);

        var result = await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = CollectionId,
            Key = "r1",
            Fields = Row("r1", ("f150", "hello"), ("f200", "world"))
        });

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task UpdateToFieldPastOldInlineLimit_ReachesSubscriber()
    {
        var (engine, publisher) = CreateEngine();
        Assert.True((await CreateCollection(engine)).Success);
        Assert.True((await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = CollectionId,
            Key = "r1",
            Fields = Row("r1", ("f150", "before"))
        })).Success);

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition { CollectionId = CollectionId },
            StartIndex = 0,
            PageSize = 10
        });

        Assert.True((await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = CollectionId,
            Key = "r1",
            Fields = new Dictionary<string, string?> { ["f150"] = "after" }
        })).Success);

        var update = publisher.EventsFor(1).OfType<RowUpdateEvent>().Single();
        Assert.Equal("r1", update.RowId);
        Assert.Equal("after", update.ChangedFields["f150"]);
    }

    [Fact]
    public async Task ProjectionOfFieldPastOldInlineLimit_ReceivesOnlyThatFieldsUpdates()
    {
        var (engine, publisher) = CreateEngine();
        Assert.True((await CreateCollection(engine)).Success);
        Assert.True((await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = CollectionId,
            Key = "r1",
            Fields = Row("r1", ("f150", "before"), ("f160", "x"))
        })).Success);

        // Projection covering a late field: exercises ViewportState.VisibleColumns past index 127.
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 2,
            View = new ViewDefinition { CollectionId = CollectionId, Fields = ["f001", "f150"] },
            StartIndex = 0,
            PageSize = 10
        });

        // Outside the projection: must not produce an update for this subscriber.
        Assert.True((await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = CollectionId,
            Key = "r1",
            Fields = new Dictionary<string, string?> { ["f160"] = "y" }
        })).Success);
        Assert.Empty(publisher.EventsFor(2).OfType<RowUpdateEvent>());

        // Inside the projection: must arrive.
        Assert.True((await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = CollectionId,
            Key = "r1",
            Fields = new Dictionary<string, string?> { ["f150"] = "after" }
        })).Success);

        var update = publisher.EventsFor(2).OfType<RowUpdateEvent>().Single();
        Assert.Equal("after", update.ChangedFields["f150"]);
    }

    [Fact]
    public async Task SortOnFieldPastOldInlineLimit_ReordersOnUpdate()
    {
        var (engine, publisher) = CreateEngine();
        Assert.True((await CreateCollection(engine)).Success);
        foreach (var (key, sortValue) in new[] { ("r1", "a"), ("r2", "b") })
        {
            Assert.True((await engine.IngestAsync(new UpsertRowCommand
            {
                CollectionId = CollectionId,
                Key = key,
                Fields = Row(key, ("f150", sortValue))
            })).Success);
        }

        // Sorting on a late field exercises SortIndex.AffectsOrder for an index past 127.
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 3,
            View = new ViewDefinition { CollectionId = CollectionId, SortColumn = "f150", SortAscending = true },
            StartIndex = 0,
            PageSize = 10
        });

        // Move r1 behind r2.
        Assert.True((await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = CollectionId,
            Key = "r1",
            Fields = new Dictionary<string, string?> { ["f150"] = "z" }
        })).Success);

        var replace = Assert.IsType<RowReplaceEvent>(Assert.Single(publisher.EventsFor(3)));
        Assert.Equal("r1", replace.RemovedRowId);
        Assert.Equal(0, replace.RemovePosition);
        Assert.Equal(1, replace.InsertPosition);
        // The reorder alone carries the new sort value - no trailing update is needed (2026-09-20 AR-01).
        Assert.Equal("z", replace.Row["f150"]);
    }
}
