using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;
using ViewEngineServer.WebApp.WebSocket;

namespace LiveViewEngine.Core.UnitTests;

public class CompactOutboundProtocolEncoderTests
{
    private static readonly CollectionSchema Schema = new("orders", ["customer", "amount", "status"]);
    private readonly CompactOutboundProtocolEncoder _encoder = new();

    [Fact]
    public void EncodeSubscriptionAccepted_UsesCompactMetadataFrame()
    {
        var payload = _encoder.EncodeSubscriptionAccepted(new SubscriptionAcceptedPayload
        {
            SubscriptionId = 7,
            Fields = ["customer", "amount"],
            SnapshotFollows = SnapshotFollowsKind.Immediate,
            StartIndex = 50,
            TotalCount = 200
        });

        Assert.Equal("A|7|1|50|200|customer|amount", ToText(payload));
    }

    [Fact]
    public void EncodeSubscriptionAccepted_CollectionDoesNotExist_UsesPendingSnapshotFollows()
    {
        var payload = _encoder.EncodeSubscriptionAccepted(new SubscriptionAcceptedPayload
        {
            SubscriptionId = 7,
            Fields = [],
            SnapshotFollows = SnapshotFollowsKind.Pending,
            StartIndex = 0,
            TotalCount = -1
        });

        Assert.Equal("A|7|2|0|-1", ToText(payload));
    }

    [Fact]
    public void EncodeFrames_EncodesSnapshotInsertUpdateDeleteAndEos()
    {
        var frames = _encoder.EncodeFrames(
            new SnapshotRowsDelta
            {
                ViewId = "1:7",
                Schema = Schema,
                StartRowNumber = 0,
                VisibleFieldIndexes = [0, 1, 2, 3],
                Rows = [["o1", "Al|ce", "100", "o\\pen"]]
            },
            subscriptionId: 7).Select(ToText).ToArray();

        Assert.Equal(["S|7|0|o1|Al\\|ce|100|o\\\\pen"], frames);

        var insert = ToText(_encoder.EncodeFrames(
            new RowInsertDelta
            {
                ViewId = "1:7",
                Schema = Schema,
                Position = 3,
                VisibleFieldIndexes = [0, 1, 2, 3],
                Row = ["o2", "Bob", "200", "open"]
            },
            subscriptionId: 7).Single());
        Assert.Equal("I|7|o2|3|Bob|200|open", insert);

        var update = ToText(_encoder.EncodeFrames(
            new RowUpdateDelta
            {
                ViewId = "1:7",
                Schema = Schema,
                RowId = "o2",
                Position = 3,
                VisibleFieldIndexes = [0, 1, 2, 3],
                ChangedColumns =
                [
                    new KeyValuePair<int, string?>(2, "250"),
                    new KeyValuePair<int, string?>(3, "")
                ]
            },
            subscriptionId: 7).Single());
        Assert.Equal("U|7|o2|3|^1|250|", update);

        var delete = ToText(_encoder.EncodeFrames(
            new RowRemoveDelta
            {
                ViewId = "1:7",
                RowId = "o2",
                Position = 3
            },
            subscriptionId: 7).Single());
        Assert.Equal("D|7|o2|3", delete);

        var replace = ToText(_encoder.EncodeFrames(
            new RowReplaceDelta
            {
                ViewId = "1:7",
                Schema = Schema,
                RemovedRowId = "o2",
                RemovePosition = 3,
                InsertPosition = 1,
                VisibleFieldIndexes = [0, 1, 2, 3],
                Row = ["o3", "Carol", "300", "open"]
            },
            subscriptionId: 7).Single());
        Assert.Equal("R|7|o2|3|1|o3|Carol|300|open", replace);

        var eos = ToText(_encoder.EncodeFrames(
            new EndOfSnapshotDelta
            {
                ViewId = "1:7"
            },
            subscriptionId: 7).Single());
        Assert.Equal("EOS|7", eos);
    }

    [Fact]
    public void EncodeSnapshotRows_BatchesMultipleRowsIntoOnePayload()
    {
        var payload = ToText(_encoder.EncodeFrames(
            new SnapshotRowsDelta
            {
                ViewId = "1:1",
                Schema = Schema,
                StartRowNumber = 10,
                VisibleFieldIndexes = [0, 1, 2],
                Rows =
                [
                    ["o1", "Alice", "100"],
                    ["o2", "Bob", "200"]
                ]
            },
            subscriptionId: 1).Single());

        Assert.Equal("S|1|10|o1|Alice|100\nS|1|11|o2|Bob|200", payload);
    }

    [Fact]
    public void EncodeSnapshotRows_EscapesEmbeddedNewlinesInsideBatchedPayload()
    {
        var payload = ToText(_encoder.EncodeFrames(
            new SnapshotRowsDelta
            {
                ViewId = "1:1",
                Schema = Schema,
                StartRowNumber = 0,
                VisibleFieldIndexes = [0, 1, 2],
                Rows =
                [
                    ["o1", "Alice\nSmith", "100"],
                    ["o2", "Bob", "200"]
                ]
            },
            subscriptionId: 1).Single());

        Assert.Equal("S|1|0|o1|Alice\\nSmith|100\nS|1|1|o2|Bob|200", payload);
    }

    [Fact]
    public void EncodeSnapshotRows_LargeBatch_IsSplitAcrossMultiplePayloads()
    {
        var largeValue = new string('x', 20_000);
        var payloads = _encoder.EncodeFrames(
            new SnapshotRowsDelta
            {
                ViewId = "1:1",
                Schema = Schema,
                StartRowNumber = 0,
                VisibleFieldIndexes = [0, 1, 2],
                Rows =
                [
                    ["o1", largeValue, "100"],
                    ["o2", largeValue, "200"]
                ]
            },
            subscriptionId: 1).Select(ToText).ToArray();

        Assert.Equal(2, payloads.Length);
        Assert.StartsWith("S|1|0|o1|", payloads[0], StringComparison.Ordinal);
        Assert.StartsWith("S|1|1|o2|", payloads[1], StringComparison.Ordinal);
    }

    [Fact]
    public void EncodeUpdate_HandlesNullValues()
    {
        var update = ToText(_encoder.EncodeFrames(
            new RowUpdateDelta
            {
                ViewId = "1:1",
                Schema = Schema,
                RowId = "o1",
                Position = 0,
                VisibleFieldIndexes = [0, 1, 2],
                ChangedColumns = [new KeyValuePair<int, string?>(2, null)]
            },
            subscriptionId: 1).Single());
        Assert.Equal("U|1|o1|0|^1|~", update);
    }

    [Fact]
    public void EncodeSnapshotRow_EscapesNullTokenInValue()
    {
        var row = ToText(_encoder.EncodeFrames(
            new SnapshotRowsDelta
            {
                ViewId = "1:1",
                Schema = Schema,
                StartRowNumber = 0,
                VisibleFieldIndexes = [0, 1, 2, 3],
                Rows = [["o1", "val~ue", "100", "~"]]
            },
            subscriptionId: 1).Single());
        Assert.Equal("S|1|0|o1|val\\~ue|100|\\~", row);
    }

    [Fact]
    public void EncodeInsert_HandlesEmptyStringValues()
    {
        var insert = ToText(_encoder.EncodeFrames(
            new RowInsertDelta
            {
                ViewId = "1:1",
                Schema = Schema,
                Position = 0,
                VisibleFieldIndexes = [0, 1, 2, 3],
                Row = ["o1", "", "100", "open"]
            },
            subscriptionId: 1).Single());
        Assert.Equal("I|1|o1|0||100|open", insert);
    }

    [Fact]
    public void EncodeSnapshotStart_WithPartialFlag()
    {
        var start = _encoder.EncodeFrames(
            new SnapshotStartDelta
            {
                ViewId = "1:1",
                StartIndex = 0,
                TotalCount = 100,
                IsPartial = true,
                Schema = Schema,
                VisibleFieldIndexes = [0, 1, 2]
            },
            subscriptionId: 1).Single();
        Assert.Equal("P|1|0|100|1|customer|amount", ToText(start));
    }

    [Fact]
    public void EncodeSnapshotStart_WithoutPartialFlag()
    {
        var start = _encoder.EncodeFrames(
            new SnapshotStartDelta
            {
                ViewId = "1:1",
                StartIndex = 10,
                TotalCount = 50,
                IsPartial = false,
                Schema = Schema,
                VisibleFieldIndexes = [0, 1, 2]
            },
            subscriptionId: 2).Single();
        Assert.Equal("P|2|10|50|0|customer|amount", ToText(start));
    }

    [Fact]
    public void EncodeSnapshotStart_WithNoChangesFlag()
    {
        var start = _encoder.EncodeFrames(
            new SnapshotStartDelta
            {
                ViewId = "1:1",
                StartIndex = 0,
                TotalCount = 100,
                IsPartial = false,
                NoChanges = true,
                Schema = Schema,
                VisibleFieldIndexes = [0, 1, 2]
            },
            subscriptionId: 1).Single();
        Assert.Equal("P|1|0|100|2|customer|amount", ToText(start));
    }

    [Fact]
    public void EncodeSnapshotRow_IncludesRowNumber()
    {
        var row = _encoder.EncodeFrames(
            new SnapshotRowsDelta
            {
                ViewId = "1:1",
                Schema = Schema,
                StartRowNumber = 50,
                VisibleFieldIndexes = [0, 1, 2],
                Rows = [["o1", "Alice", "100"]]
            },
            subscriptionId: 1).Single();

        Assert.Equal("S|1|50|o1|Alice|100", ToText(row));
    }

    [Fact]
    public void EncodeUpdate_WithAllFieldsSkipped()
    {
        var update = ToText(_encoder.EncodeFrames(
            new RowUpdateDelta
            {
                ViewId = "1:1",
                Schema = Schema,
                RowId = "o1",
                Position = 5,
                VisibleFieldIndexes = [0, 1, 2],
                ChangedColumns = []
            },
            subscriptionId: 1).Single());
        Assert.Equal("U|1|o1|5|^2", update);
    }

    private static string ToText(byte[] payload) => System.Text.Encoding.UTF8.GetString(payload);
}
