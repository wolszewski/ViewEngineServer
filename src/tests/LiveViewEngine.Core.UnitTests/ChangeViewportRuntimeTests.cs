using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Runtime;
using LiveViewEngine.Core.Views;

namespace LiveViewEngine.Core.UnitTests;

public class UpdateViewRuntimeTests
{
    private static CollectionRuntime MakeRuntime(int rowCount)
    {
        var schema = new CollectionSchema("trades", ["value", "other"]);
        var collection = new RowCollection(schema);
        var runtime = new CollectionRuntime(collection, null, new LiveViewEngineOptions { EagerIndexing = false });

        for (var i = 0; i < rowCount; i++)
        {
            var key = $"row-{i:D3}";
            runtime.HandleUpsert(new UpsertRowCommand
            {
                CollectionId = schema.CollectionName,
                Key = key,
                Fields = new Dictionary<string, string?>
                {
                    ["value"] = key,
                    ["other"] = $"other-{key}"
                }
            });
        }

        return runtime;
    }

    private static void Subscribe(CollectionRuntime runtime, int startIndex, int pageSize, int connectionId = 1, int subscriptionId = 1)
    {
        runtime.HandleSubscribe(new SubscribeCommand
        {
            ConnectionId = connectionId,
            SubscriptionId = subscriptionId,
            StartIndex = startIndex,
            PageSize = pageSize,
            View = new ViewDefinition
            {
                CollectionId = "trades"
            }
        });
    }

    [Fact]
    public void UpdateView_NullPageSize_InheritsPreviousWindow()
    {
        var runtime = MakeRuntime(200);
        Subscribe(runtime, 0, 50);

        var deltas = runtime.HandleUpdateView(new UpdateViewCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            StartIndex = 20,
            PageSize = null
        });

        var start = Assert.IsType<SnapshotStartDelta>(deltas[0]);
        Assert.True(start.IsPartial);
        Assert.Equal(50, start.StartIndex);

        var rows = Assert.IsType<SnapshotRowsDelta>(deltas[1]);
        Assert.Equal(20, rows.Rows.Count);
        Assert.IsType<EndOfSnapshotDelta>(deltas[^1]);
    }

    [Fact]
    public void UpdateView_WhenViewportExpandsRight_ReturnsPartialSnapshotForNewRightRows()
    {
        var runtime = MakeRuntime(200);
        Subscribe(runtime, 0, 50);

        var deltas = runtime.HandleUpdateView(new UpdateViewCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            StartIndex = 0,
            PageSize = 100
        });

        var start = Assert.IsType<SnapshotStartDelta>(deltas[0]);
        Assert.True(start.IsPartial);
        Assert.Equal(50, start.StartIndex);

        var rows = Assert.IsType<SnapshotRowsDelta>(deltas[1]);
        Assert.Equal(50, rows.Rows.Count);
        Assert.IsType<EndOfSnapshotDelta>(deltas[^1]);
    }

    [Fact]
    public void UpdateView_WhenViewportExpandsRight_EmitsPartialStreamStart()
    {
        var runtime = MakeRuntime(200);
        Subscribe(runtime, 0, 50);

        var deltas = runtime.HandleUpdateView(new UpdateViewCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            StartIndex = 0,
            PageSize = 100
        });

        var start = Assert.IsType<SnapshotStartDelta>(deltas[0]);
        Assert.True(start.IsPartial);
        Assert.Equal(50, start.StartIndex);

        var rows = Assert.IsType<SnapshotRowsDelta>(deltas[1]);
        Assert.Equal(50, rows.Rows.Count);
        Assert.IsType<EndOfSnapshotDelta>(deltas[^1]);
    }

    [Fact]
    public void UpdateView_WhenSubscriptionDoesNotExist_Throws()
    {
        var runtime = MakeRuntime(200);

        var ex = Assert.Throws<InvalidOperationException>(() => runtime.HandleUpdateView(new UpdateViewCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            StartIndex = 0,
            PageSize = 50
        }));

        Assert.Contains("was not found", ex.Message);
    }

    [Fact]
    public void UpdateView_WhenFieldsCleared_ReturnsAllFields()
    {
        var runtime = MakeRuntime(10);
        Subscribe(runtime, 0, 5);

        runtime.HandleUpdateView(new UpdateViewCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            Fields = ["value"]
        });

        var deltas = runtime.HandleUpdateView(new UpdateViewCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            Fields = []
        });

        var rows = Assert.IsType<SnapshotRowsDelta>(deltas[1]);
        Assert.Equal(3, rows.Rows[0].Length);
    }

    [Fact]
    public void UpdateView_ViewportOnlyChange_KeepsExistingProjection()
    {
        var runtime = MakeRuntime(10);
        runtime.HandleSubscribe(new SubscribeCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            StartIndex = 0,
            PageSize = 5,
            View = new ViewDefinition
            {
                CollectionId = "trades",
                Fields = ["value"]
            }
        });

        var deltas = runtime.HandleUpdateView(new UpdateViewCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            StartIndex = 5,
            PageSize = null
        });

        var rows = Assert.IsType<SnapshotRowsDelta>(deltas[1]);
        Assert.Equal(2, rows.Rows[0].Length);
    }
}
