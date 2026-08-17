using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Output;

namespace LiveViewEngine.Core.UnitTests;

public class JsonOutboundEventFormatterTests
{
    [Fact]
    public void Format_AssignsSubscriptionIdToAllEvents()
    {
        var schema = new CollectionSchema("orders", ["amount"]);
        var formatter = new JsonOutboundEventFormatter();

        var events = formatter.Format(
        [
            new SnapshotDelta
            {
                ViewId = "internal-subscription",
                Schema = schema,
                TotalCount = 1,
                StartIndex = 0,
                Rows = [["o1", "100"]],
                VisibleFieldIndexes = [0, 1]
            },
            new RowInsertDelta
            {
                ViewId = "internal-subscription",
                Schema = schema,
                Position = 0,
                Row = ["o1", "100"],
                VisibleFieldIndexes = [0, 1]
            }
        ], subscriptionId: 7);

        Assert.All(events, static e => Assert.Equal(7, e.SubscriptionId));
    }
}
