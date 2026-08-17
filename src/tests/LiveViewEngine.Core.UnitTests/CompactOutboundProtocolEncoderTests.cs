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
            SnapshotFollows = true,
            StartIndex = 50,
            TotalCount = 200
        });

        Assert.Equal("A|7|1|50|200|customer|amount", ToText(payload));
    }

    [Fact]
    public void EncodeFrames_EncodesSnapshotInsertUpdateDeleteAndEos()
    {
        var frames = _encoder.EncodeFrames(
            new SnapshotRowsDelta
            {
                ViewId = "1:7",
                Schema = Schema,
                VisibleFieldIndexes = [0, 1, 2, 3],
                Rows = [["o1", "Al|ce", "100", "o\\pen"]]
            },
            subscriptionId: 7).Select(ToText).ToArray();

        Assert.Equal(["S|7|o1|Al\\|ce|100|o\\\\pen"], frames);

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

        var eos = ToText(_encoder.EncodeFrames(
            new EndOfSnapshotDelta
            {
                ViewId = "1:7"
            },
            subscriptionId: 7).Single());
        Assert.Equal("EOS|7", eos);
    }

    private static string ToText(byte[] payload) => System.Text.Encoding.UTF8.GetString(payload);
}
