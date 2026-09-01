using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.DataIngest;
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
            PageSize = null,
            SnapshotMode = SnapshotMode.Delta
        });

        var start = Assert.IsType<SnapshotStartDelta>(deltas[0]);
        Assert.True(start.IsPartial);
        Assert.Equal(50, start.StartIndex);

        var rows = Assert.IsType<SnapshotRowsDelta>(deltas[1]);
        Assert.Equal(50, rows.StartRowNumber);
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
            PageSize = 100,
            SnapshotMode = SnapshotMode.Delta
        });

        var start = Assert.IsType<SnapshotStartDelta>(deltas[0]);
        Assert.True(start.IsPartial);
        Assert.Equal(50, start.StartIndex);

        var rows = Assert.IsType<SnapshotRowsDelta>(deltas[1]);
        Assert.Equal(50, rows.StartRowNumber);
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
            PageSize = 100,
            SnapshotMode = SnapshotMode.Delta
        });

        var start = Assert.IsType<SnapshotStartDelta>(deltas[0]);
        Assert.True(start.IsPartial);
        Assert.Equal(50, start.StartIndex);

        var rows = Assert.IsType<SnapshotRowsDelta>(deltas[1]);
        Assert.Equal(50, rows.StartRowNumber);
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

    [Fact]
    public void UpdateView_WhenProjectionContainsUnknownField_DoesNotDetachExistingSubscription()
    {
        var runtime = MakeRuntime(10);
        Subscribe(runtime, 0, 5);

        var ex = Assert.Throws<ArgumentException>(() => runtime.HandleUpdateView(new UpdateViewCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            Fields = ["value", "missing-field"]
        }));

        Assert.Contains("Unknown field 'missing-field'", ex.Message);
        Assert.True(runtime.ContainsSubscription(new SubscriptionKey(1, 1)));
        Assert.Equal(1, runtime.ActiveSubscriptionCount);
    }

    [Fact]
    public void UpdateView_WithSnapshotModeFull_AndIdenticalViewport_ReturnsFullSnapshot()
    {
        var runtime = MakeRuntime(100);
        Subscribe(runtime, 0, 50);

        var deltas = runtime.HandleUpdateView(new UpdateViewCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            StartIndex = 0,
            PageSize = 50,
            SnapshotMode = SnapshotMode.Full
        });

        var start = Assert.IsType<SnapshotStartDelta>(deltas[0]);
        Assert.False(start.IsPartial);
        Assert.Equal(0, start.StartIndex);

        var rows = Assert.IsType<SnapshotRowsDelta>(deltas[1]);
        Assert.Equal(0, rows.StartRowNumber);
        Assert.Equal(50, rows.Rows.Count);
        Assert.IsType<EndOfSnapshotDelta>(deltas[^1]);
    }

    [Fact]
    public void UpdateView_WithSnapshotModeFull_AndPageSizeIncrease_ReturnsFullSnapshot()
    {
        var runtime = MakeRuntime(200);
        Subscribe(runtime, 0, 50);

        var deltas = runtime.HandleUpdateView(new UpdateViewCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            StartIndex = 0,
            PageSize = 100,
            SnapshotMode = SnapshotMode.Full
        });

        var start = Assert.IsType<SnapshotStartDelta>(deltas[0]);
        Assert.False(start.IsPartial);
        Assert.Equal(0, start.StartIndex);

        var rows = Assert.IsType<SnapshotRowsDelta>(deltas[1]);
        Assert.Equal(0, rows.StartRowNumber);
        Assert.Equal(100, rows.Rows.Count);
        Assert.IsType<EndOfSnapshotDelta>(deltas[^1]);

    }

    [Fact]
    public void UpdateView_WithSameViewDefinitionAndExpandedViewport_SendsOnlyMissingRows()
    {
        var runtime = MakeRuntime(500);
        runtime.HandleSubscribe(new SubscribeCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            StartIndex = 0,
            PageSize = 200,
            View = new ViewDefinition
            {
                CollectionId = "trades",
                SortColumn = "value",
                SortAscending = true
            }
        });

        var deltas = runtime.HandleUpdateView(new UpdateViewCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            StartIndex = 0,
            PageSize = 400,
            SortColumn = "value",
            SortAscending = true,
            SnapshotMode = SnapshotMode.Delta
        });

        Assert.Equal(4, deltas.Count);
        var start = Assert.IsType<SnapshotStartDelta>(deltas[0]);
        Assert.True(start.IsPartial);
        Assert.Equal(200, start.StartIndex);

        var rows = Assert.IsType<SnapshotRowsDelta>(deltas[1]);
        var tailRows = deltas.OfType<SnapshotRowsDelta>().SelectMany(static delta => delta.Rows).ToArray();
        var tailRowNumbers = deltas.OfType<SnapshotRowsDelta>()
            .SelectMany(static delta => Enumerable.Range(delta.StartRowNumber, delta.Rows.Count))
            .ToArray();
        Assert.Equal(200, tailRows.Length);
        Assert.Equal(Enumerable.Range(200, 200).ToArray(), tailRowNumbers);
        Assert.Equal("row-200", rows.Rows[0][1]);
        Assert.Equal("row-399", tailRows[^1][1]);
        Assert.IsType<EndOfSnapshotDelta>(deltas[^1]);
    }

    [Fact]
    public void UpdateView_WithSnapshotModeNo_AndIdenticalViewport_ReturnsEmpty()
    {
        var runtime = MakeRuntime(100);
        Subscribe(runtime, 0, 50);

        var deltas = runtime.HandleUpdateView(new UpdateViewCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            StartIndex = 0,
            PageSize = 50,
            SnapshotMode = SnapshotMode.No
        });

        Assert.Empty(deltas);
    }

    [Fact]
    public void UpdateView_WithSnapshotModeDelta_AndIdenticalViewport_ReturnsEmpty()
    {
        var runtime = MakeRuntime(100);
        Subscribe(runtime, 0, 50);

        var deltas = runtime.HandleUpdateView(new UpdateViewCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            StartIndex = 0,
            PageSize = 50,
            SnapshotMode = SnapshotMode.Delta
        });

        Assert.Empty(deltas);
    }
}
