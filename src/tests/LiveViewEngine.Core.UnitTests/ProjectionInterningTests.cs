using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.DataIngest;
using LiveViewEngine.Core.Runtime;
using LiveViewEngine.Core.Views;

namespace LiveViewEngine.Core.UnitTests;

// Schema width is unbounded since AR-08, so the projection interning table has to shrink with
// subscription churn instead of retaining a key array per distinct field subset for the
// collection's lifetime.
public class ProjectionInterningTests
{
    private const string CollectionId = "trades";
    private static readonly string[] FieldNames =
        [.. Enumerable.Range(0, 10).Select(i => $"f{i:D2}")];

    private static CollectionRuntime MakeRuntime()
    {
        var collection = new RowCollection(new CollectionSchema(CollectionId, FieldNames));
        var runtime = new CollectionRuntime(collection, null, new LiveViewEngineOptions { EagerIndexing = false });

        runtime.HandleUpsert(new UpsertRowCommand
        {
            CollectionId = CollectionId,
            Key = "r1",
            Fields = FieldNames.ToDictionary(f => f, f => (string?)$"{f}-value")
        });

        return runtime;
    }

    private static void Subscribe(CollectionRuntime runtime, int subscriptionId, params string[] fields) =>
        runtime.HandleSubscribe(new SubscribeCommand
        {
            ConnectionId = 1,
            SubscriptionId = subscriptionId,
            StartIndex = 0,
            PageSize = 10,
            SendSnapshot = false,
            View = new ViewDefinition { CollectionId = CollectionId, Fields = fields }
        });

    private static void Unsubscribe(CollectionRuntime runtime, int subscriptionId) =>
        runtime.HandleUnsubscribe(new UnsubscribeCommand { ConnectionId = 1, SubscriptionId = subscriptionId });

    [Fact]
    public void Unsubscribe_ReleasesTheProjection()
    {
        var runtime = MakeRuntime();

        Subscribe(runtime, 1, "f00", "f03");
        Assert.Equal(1, runtime.InternedProjectionCount);

        Unsubscribe(runtime, 1);
        Assert.Equal(0, runtime.InternedProjectionCount);
    }

    [Fact]
    public void SubscriptionChurn_OverDistinctProjections_DoesNotGrowTheTable()
    {
        var runtime = MakeRuntime();

        // Each iteration picks a different field subset, the shape that previously interned a new
        // key array on every subscribe and never released one.
        for (var i = 1; i < FieldNames.Length; i++)
        {
            Subscribe(runtime, i, "f00", FieldNames[i]);
            Assert.Equal(1, runtime.InternedProjectionCount);
            Unsubscribe(runtime, i);
            Assert.Equal(0, runtime.InternedProjectionCount);
        }
    }

    [Fact]
    public void SharedProjection_SurvivesUntilTheLastSubscriberDetaches()
    {
        var runtime = MakeRuntime();

        Subscribe(runtime, 1, "f00", "f03");
        Subscribe(runtime, 2, "f00", "f03");
        Assert.Equal(1, runtime.InternedProjectionCount);

        Unsubscribe(runtime, 1);
        Assert.Equal(1, runtime.InternedProjectionCount);

        Unsubscribe(runtime, 2);
        Assert.Equal(0, runtime.InternedProjectionCount);
    }

    [Fact]
    public void Resubscribe_WithADifferentProjection_ReleasesTheReplacedOne()
    {
        var runtime = MakeRuntime();

        Subscribe(runtime, 1, "f00", "f03");
        Subscribe(runtime, 1, "f00", "f07");

        Assert.Equal(1, runtime.InternedProjectionCount);

        Unsubscribe(runtime, 1);
        Assert.Equal(0, runtime.InternedProjectionCount);
    }

    [Fact]
    public void UnsubscribeAll_ForAConnection_ReleasesEveryProjection()
    {
        var runtime = MakeRuntime();

        Subscribe(runtime, 1, "f00", "f03");
        Subscribe(runtime, 2, "f00", "f07");
        Assert.Equal(2, runtime.InternedProjectionCount);

        // SubscriptionId 0 is the "drop everything on this connection" path.
        runtime.HandleUnsubscribe(new UnsubscribeCommand { ConnectionId = 1, SubscriptionId = 0 });

        Assert.Equal(0, runtime.InternedProjectionCount);
    }
}
