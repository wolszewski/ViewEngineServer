using LiveViewEngine.TcpProtocol;

namespace LiveViewEngine.Core.UnitTests;

public class TcpProtocolCodecTests
{
    [Fact]
    public void SerializeAndParseUpsert_RoundTripsIndexedFieldValues()
    {
        var request = new UpsertRequestMessage(
            42,
            "trades",
            "trade|1",
            [
                new KeyValuePair<int, string?>(1, "value with spaces"),
                new KeyValuePair<int, string?>(2, null),
                new KeyValuePair<int, string?>(3, string.Empty)
            ]);

        var encoded = TcpProtocolCodec.SerializeRequest(request);

        var parsed = Assert.IsType<UpsertRequestMessage>(TcpProtocolCodec.ParseRequest(encoded));
        Assert.Equal(42, parsed.RequestId);
        Assert.Equal("trades", parsed.CollectionName);
        Assert.Equal("trade|1", parsed.RowKey);
        Assert.Equal(3, parsed.Fields.Count);
        Assert.Equal("value with spaces", parsed.Fields[0].Value);
        Assert.Null(parsed.Fields[1].Value);
        Assert.Equal(string.Empty, parsed.Fields[2].Value);
    }

    [Fact]
    public void SerializeAndParseSchemaResponse_RoundTripsFields()
    {
        var response = new SchemaResponseMessage(
            7,
            "orders",
            [
                new TcpSchemaField(0, "key", "string"),
                new TcpSchemaField(1, "price", "decimal"),
                new TcpSchemaField(2, "status", "string")
            ]);

        var encoded = TcpProtocolCodec.SerializeResponse(response);

        var parsed = Assert.IsType<SchemaResponseMessage>(TcpProtocolCodec.ParseResponse(encoded));
        Assert.Equal(7, parsed.RequestId);
        Assert.Equal("orders", parsed.CollectionName);
        Assert.Collection(
            parsed.Fields,
            field =>
            {
                Assert.Equal(0, field.Index);
                Assert.Equal("key", field.Name);
                Assert.Equal("string", field.Type);
            },
            field =>
            {
                Assert.Equal(1, field.Index);
                Assert.Equal("price", field.Name);
                Assert.Equal("decimal", field.Type);
            },
            field =>
            {
                Assert.Equal(2, field.Index);
                Assert.Equal("status", field.Name);
                Assert.Equal("string", field.Type);
            });
    }

    [Fact]
    public void SerializeAndParseAckResponse_RoundTripsRequestIdAndOperation()
    {
        var response = new AckResponseMessage(42, "UPSERT");

        var encoded = TcpProtocolCodec.SerializeResponse(response);

        var parsed = Assert.IsType<AckResponseMessage>(TcpProtocolCodec.ParseResponse(encoded));
        Assert.Equal(42, parsed.RequestId);
        Assert.Equal("UPSERT", parsed.Operation);
    }
}
