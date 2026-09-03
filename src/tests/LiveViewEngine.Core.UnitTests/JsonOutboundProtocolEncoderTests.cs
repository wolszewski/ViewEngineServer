using System.Text;
using System.Text.Json;
using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;
using ViewEngineServer.WebApp.WebSocket;

namespace LiveViewEngine.Core.UnitTests;

public class JsonOutboundProtocolEncoderTests
{
    private static readonly CollectionSchema Schema = new("orders", ["customer", "amount"]);
    private readonly JsonOutboundProtocolEncoder _encoder = new();

    [Fact]
    public void EncodeSubscriptionAccepted_UsesJsonMetadata()
    {
        var payload = _encoder.EncodeSubscriptionAccepted(new SubscriptionAcceptedPayload
        {
            SubscriptionId = 9,
            Fields = ["customer", "amount"],
            SnapshotFollows = true,
            StartIndex = 20,
            TotalCount = 100
        });

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal("subscriptionAccepted", root.GetProperty("type").GetString());
        Assert.Equal(9, root.GetProperty("subscriptionId").GetInt32());
        Assert.True(root.GetProperty("snapshotFollows").GetBoolean());
        Assert.Equal(20, root.GetProperty("startIndex").GetInt32());
        Assert.Equal(100, root.GetProperty("totalCount").GetInt32());
        var fields = root.GetProperty("fields").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Collection(fields,
            value => Assert.Equal("customer", value),
            value => Assert.Equal("amount", value));
    }

    [Fact]
    public void EncodeSubscriptionRejected_UsesJsonMetadata()
    {
        var payload = _encoder.EncodeSubscriptionRejected(new SubscriptionRejectedPayload
        {
            SubscriptionId = 4,
            Reason = "collection_not_found",
            Message = "Collection 'trades' does not exist."
        });

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal("subscriptionRejected", root.GetProperty("type").GetString());
        Assert.Equal(4, root.GetProperty("subscriptionId").GetInt32());
        Assert.Equal("collection_not_found", root.GetProperty("reason").GetString());
        Assert.Equal("Collection 'trades' does not exist.", root.GetProperty("message").GetString());
    }

    [Fact]
    public void EncodeFrames_StreamsSnapshotRowsAndLiveDeltasAsSingleJsonMessages()
    {
        var frames = _encoder.EncodeFrames(
            new SnapshotDelta
            {
                ViewId = "1:9",
                Schema = Schema,
                TotalCount = 2,
                StartIndex = 0,
                VisibleFieldIndexes = [0, 1, 2],
                Rows =
                [
                    ["o1", "Alice", "100"],
                    ["o2", "Bob", "200"]
                ]
            },
            9).Select(frame => Encoding.UTF8.GetString(frame)).ToArray();

        AssertMessage(frames[0], "snapshotStart", 9, ("startIndex", "0"), ("totalCount", "2"), ("fields", "[\"customer\",\"amount\"]"));
        AssertMessage(frames[1], "snapshotRow", 9, ("rowNumber", "0"), ("row", "{\"key\":\"o1\",\"customer\":\"Alice\",\"amount\":\"100\"}"));
        AssertMessage(frames[2], "snapshotRow", 9, ("rowNumber", "1"), ("row", "{\"key\":\"o2\",\"customer\":\"Bob\",\"amount\":\"200\"}"));
        AssertMessage(frames[3], "eos", 9);

        var update = Encoding.UTF8.GetString(_encoder.EncodeFrames(
            new RowUpdateDelta
            {
                ViewId = "1:9",
                Schema = Schema,
                RowId = "o2",
                Position = 1,
                VisibleFieldIndexes = [0, 1, 2],
                ChangedColumns = [new KeyValuePair<int, string?>(2, "250")]
            },
            9).Single());
        AssertMessage(update, "rowUpdate", 9, ("rowId", "\"o2\""), ("position", "1"), ("changedFields", "{\"amount\":\"250\"}"));

        var replace = Encoding.UTF8.GetString(_encoder.EncodeFrames(
            new RowReplaceDelta
            {
                ViewId = "1:9",
                Schema = Schema,
                RemovedRowId = "o2",
                RemovePosition = 1,
                InsertPosition = 0,
                VisibleFieldIndexes = [0, 1, 2],
                Row = ["o3", "Carol", "300"]
            },
            9).Single());
        AssertMessage(
            replace,
            "rowReplace",
            9,
            ("removedRowId", "\"o2\""),
            ("removePosition", "1"),
            ("insertPosition", "0"),
            ("row", "{\"key\":\"o3\",\"customer\":\"Carol\",\"amount\":\"300\"}"));
    }

    private static void AssertMessage(string json, string type, int subscriptionId, params (string Name, string Value)[] properties)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(type, root.GetProperty("type").GetString());
        Assert.Equal(subscriptionId, root.GetProperty("subscriptionId").GetInt32());
        foreach (var (name, value) in properties)
        {
            Assert.Equal(value, root.GetProperty(name).GetRawText());
        }
    }
}
