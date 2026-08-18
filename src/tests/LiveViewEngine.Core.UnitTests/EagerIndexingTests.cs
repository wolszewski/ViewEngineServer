using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Runtime;
using LiveViewEngine.Core.Views;

namespace LiveViewEngine.Core.UnitTests;

public class EagerIndexingTests
{
    private static CollectionSchema MixedSchema() =>
        new("test", ["score", "label", "ts"],
            [ScalarFieldType.Int32, ScalarFieldType.String, ScalarFieldType.DateTime]);

    private static CollectionRuntime MakeRuntime(CollectionSchema schema, LiveViewEngineOptions options)
    {
        var collection = new RowCollection(schema);
        return new CollectionRuntime(collection, null, options);
    }

    [Fact]
    public void EagerIndexingDisabled_SortIndexesNotPreCreated()
    {
        var runtime = MakeRuntime(MixedSchema(), new LiveViewEngineOptions { EagerIndexing = false });

        Assert.Equal(0, runtime.SortIndexCount);
    }

    [Fact]
    public void EagerIndexingDisabled_TypedColumnsNotPreActivated()
    {
        var schema = MixedSchema();
        var collection = new RowCollection(schema);
        new CollectionRuntime(collection, null, new LiveViewEngineOptions { EagerIndexing = false });

        var scoreIndex = schema.GetFieldIndex("score");
        var tsIndex = schema.GetFieldIndex("ts");

        Assert.False(collection.IsTypedFieldActivated(scoreIndex));
        Assert.False(collection.IsTypedFieldActivated(tsIndex));
    }

    [Fact]
    public void EagerIndexing_SortIndexCreatedForEveryField()
    {
        var schema = MixedSchema();
        var runtime = MakeRuntime(schema, new LiveViewEngineOptions { EagerIndexing = true });

        Assert.Equal(schema.Fields.Count, runtime.SortIndexCount);
    }

    [Fact]
    public void EagerIndexing_TypedColumnsActivatedForAllNonStringFields()
    {
        var schema = MixedSchema();
        var collection = new RowCollection(schema);
        new CollectionRuntime(collection, null, new LiveViewEngineOptions { EagerIndexing = true });

        foreach (var field in schema.Fields)
        {
            if (field.Type == ScalarFieldType.String)
            {
                continue;
            }

            Assert.True(collection.IsTypedFieldActivated(field.FieldIndex),
                $"Expected typed column for field '{field.Name}' (type {field.Type}) to be activated.");
        }
    }

    [Fact]
    public void EagerIndexing_StringFieldsHaveNoTypedColumns()
    {
        var schema = MixedSchema();
        var collection = new RowCollection(schema);
        new CollectionRuntime(collection, null, new LiveViewEngineOptions { EagerIndexing = true });

        foreach (var field in schema.Fields.Where(f => f.Type == ScalarFieldType.String))
        {
            Assert.False(collection.IsTypedFieldActivated(field.FieldIndex),
                $"String field '{field.Name}' should not have an activated typed column.");
        }
    }

    [Fact]
    public async Task EagerIndexing_StaleIndexReaperServiceExitsImmediately()
    {
        var store = new CollectionStore(null, new LiveViewEngineOptions { EagerIndexing = false });
        var options = new LiveViewEngineOptions { EagerIndexing = true };
        var service = new StaleIndexReaperService(store, options);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        Assert.True(service.ExecuteTask?.IsCompleted,
            "StaleIndexReaperService should exit without waiting for cancellation when EagerIndexing is true.");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void EagerIndexing_SnapshotPagingUsesUpToDateSortIndex()
    {
        var schema = MixedSchema();
        var runtime = MakeRuntime(schema, new LiveViewEngineOptions { EagerIndexing = true });

        for (var i = 0; i < 100; i++)
        {
            var result = runtime.HandleUpsert(new UpsertRowCommand
            {
                CollectionId = schema.CollectionName,
                Key = $"row-{i:D3}",
                Fields = new Dictionary<string, string?>
                {
                    ["score"] = i.ToString(),
                    ["label"] = $"label-{i:D3}",
                    ["ts"] = $"2026-01-{(i % 28) + 1:D2}T00:00:00Z"
                }
            });

            Assert.True(result.Result.Success, result.Result.Error);
        }

        var deltas = runtime.HandleSubscribe(new SubscribeCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            StartIndex = 50,
            PageSize = 10,
            View = new ViewDefinition
            {
                CollectionId = schema.CollectionName,
                SortColumn = "score"
            }
        });

        var snapshot = Assert.IsType<SnapshotDelta>(Assert.Single(deltas));
        Assert.Equal(100, snapshot.TotalCount);
        Assert.Equal(10, snapshot.Rows.Count);
        Assert.Equal("row-050", snapshot.Rows[0][0]);
        Assert.Equal("row-059", snapshot.Rows[^1][0]);
    }
}
