using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Runtime;
using LiveViewEngine.Core.Views;

namespace LiveViewEngine.Core.UnitTests;

public class ChangeViewportRuntimeTests
{
    private static CollectionRuntime MakeRuntime(int rowCount)
    {
        var schema = new CollectionSchema("trades", ["value"]);
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
                    ["value"] = key
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
    public void ChangeViewport_NullPageSize_InheritsPreviousWindow()
    {
        var runtime = MakeRuntime(200);
        Subscribe(runtime, 0, 50);

        var deltas = runtime.HandleChangeViewport(new ChangeViewportCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            StartIndex = 20,
            PageSize = null
        });

        var snapshot = Assert.IsType<SnapshotDelta>(Assert.Single(deltas));
        Assert.True(snapshot.IsPartial);
        Assert.Equal(50, snapshot.StartIndex);
        Assert.Equal(20, snapshot.Rows.Count);
    }

    [Fact]
    public void ChangeViewport_WhenViewportExpandsRight_ReturnsPartialSnapshotForNewRightRows()
    {
        var runtime = MakeRuntime(200);
        Subscribe(runtime, 0, 50);

        var deltas = runtime.HandleChangeViewport(new ChangeViewportCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            StartIndex = 0,
            PageSize = 100
        });

        var snapshot = Assert.IsType<SnapshotDelta>(Assert.Single(deltas));
        Assert.True(snapshot.IsPartial);
        Assert.Equal(50, snapshot.StartIndex);
        Assert.Equal(50, snapshot.Rows.Count);
    }

    [Fact]
    public void ChangeViewport_StreamSnapshot_WhenViewportExpandsRight_EmitsPartialStreamStart()
    {
        var runtime = MakeRuntime(200);
        Subscribe(runtime, 0, 50);

        var deltas = runtime.HandleChangeViewport(new ChangeViewportCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            StartIndex = 0,
            PageSize = 100,
            StreamSnapshot = true
        });

        var start = Assert.IsType<SnapshotStartDelta>(deltas[0]);
        Assert.True(start.IsPartial);
        Assert.Equal(50, start.StartIndex);

        var rows = Assert.IsType<SnapshotRowsDelta>(deltas[1]);
        Assert.Equal(50, rows.Rows.Count);
        Assert.IsType<EndOfSnapshotDelta>(deltas[^1]);
    }
}
