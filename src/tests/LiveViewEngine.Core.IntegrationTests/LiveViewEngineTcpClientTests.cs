using System.Net;
using System.Net.Sockets;
using System.Text;
using LiveViewEngine.TcpClient;
using LiveViewEngine.TcpProtocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveViewEngine.Core.IntegrationTests;

public class LiveViewEngineTcpClientTests
{
    [Fact]
    public async Task IngestAsync_RequestsSchemaOnceAndThenUsesIndexedUpdates()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunFakeServerAsync(listener, cts.Token);
        var client = new LiveViewEngineTcpClient(
            new LiveViewEngineTcpClientOptions
            {
                Host = "127.0.0.1",
                Port = port,
                RequestTimeout = TimeSpan.FromSeconds(5),
                ReconnectDelay = TimeSpan.FromMilliseconds(100)
            },
            NullLogger<LiveViewEngineTcpClient>.Instance);

        var runTask = client.RunAsync(cts.Token);

        var firstResult = await client.IngestAsync(
            "trades",
            "trade-1",
            new Dictionary<string, string?>
            {
                ["tradeId"] = "T000001",
                ["price"] = "101.25"
            },
            cts.Token);

        var secondResult = await client.IngestAsync(
            "trades",
            "trade-1",
            new Dictionary<string, string?>
            {
                ["price"] = "102.00"
            },
            cts.Token);

        Assert.True(firstResult);
        Assert.True(secondResult);

        await serverTask;
        cts.Cancel();
        await runTask;
    }

    private static async Task RunFakeServerAsync(TcpListener listener, CancellationToken ct)
    {
        using var acceptedClient = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
        await using var stream = acceptedClient.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            AutoFlush = true
        };

        await writer.WriteLineAsync(
            TcpProtocolCodec.SerializeResponse(new ConnectedResponseMessage(TcpProtocolCodec.ProtocolVersion)))
            .ConfigureAwait(false);

        var schemaLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        var schemaRequest = Assert.IsType<GetSchemaRequestMessage>(
            TcpProtocolCodec.ParseRequest(schemaLine ?? throw new InvalidOperationException("Missing schema request.")));
        Assert.Equal("trades", schemaRequest.CollectionName);

        await writer.WriteLineAsync(
            TcpProtocolCodec.SerializeResponse(
                new SchemaResponseMessage(
                    schemaRequest.RequestId,
                    "trades",
                    [
                        new TcpSchemaField(0, "key", "string"),
                        new TcpSchemaField(1, "tradeId", "string"),
                        new TcpSchemaField(2, "price", "decimal")
                    ]))).ConfigureAwait(false);

        var firstUpsertLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        var firstUpsert = Assert.IsType<UpsertRequestMessage>(
            TcpProtocolCodec.ParseRequest(firstUpsertLine ?? throw new InvalidOperationException("Missing first upsert.")));
        Assert.Equal("trade-1", firstUpsert.RowKey);
        var firstFields = firstUpsert.Fields.ToDictionary(static field => field.Key, static field => field.Value);
        Assert.Equal("T000001", firstFields[1]);
        Assert.Equal("101.25", firstFields[2]);

        var secondUpsertLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        var secondUpsert = Assert.IsType<UpsertRequestMessage>(
            TcpProtocolCodec.ParseRequest(secondUpsertLine ?? throw new InvalidOperationException("Missing second upsert.")));
        var secondFields = secondUpsert.Fields.ToDictionary(static field => field.Key, static field => field.Value);
        Assert.Equal("102.00", secondFields[2]);

        await writer.WriteLineAsync(
            TcpProtocolCodec.SerializeResponse(new AckResponseMessage(firstUpsert.RequestId, "UPSERT")))
            .ConfigureAwait(false);
        await writer.WriteLineAsync(
            TcpProtocolCodec.SerializeResponse(new AckResponseMessage(secondUpsert.RequestId, "UPSERT")))
            .ConfigureAwait(false);

        listener.Stop();
    }
}
