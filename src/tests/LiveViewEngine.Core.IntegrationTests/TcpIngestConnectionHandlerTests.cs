using System.Net;
using System.Net.Sockets;
using System.Text;
using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Output;
using LiveViewEngine.TcpProtocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ViewEngineServer.WebApp.Tcp;

namespace LiveViewEngine.Core.IntegrationTests;

public class TcpIngestConnectionHandlerTests
{
    [Fact]
    public async Task HandleAsync_RejectsOversizedFrames()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var acceptTask = listener.AcceptTcpClientAsync(cts.Token);

        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        using var serverClient = await acceptTask;

        var handler = CreateHandler(maxFrameLengthBytes: 32);
        var handleTask = handler.HandleAsync(serverClient, cts.Token);

        await using var clientStream = client.GetStream();
        using var reader = new StreamReader(clientStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(clientStream, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            AutoFlush = true
        };

        var connectedLine = await reader.ReadLineAsync(cts.Token);
        Assert.Equal(
            TcpProtocolCodec.SerializeResponse(new ConnectedResponseMessage(TcpProtocolCodec.ProtocolVersion)),
            connectedLine);

        await writer.WriteLineAsync(new string('x', 33));

        var errorLine = await reader.ReadLineAsync(cts.Token);
        var error = Assert.IsType<ErrorResponseMessage>(
            TcpProtocolCodec.ParseResponse(errorLine ?? throw new InvalidOperationException("Missing oversized-frame error.")));
        Assert.Equal(0, error.RequestId);
        Assert.Contains("32", error.Message, StringComparison.Ordinal);

        Assert.Null(await reader.ReadLineAsync(cts.Token));
        await handleTask;
    }

    private static TcpIngestConnectionHandler CreateHandler(int maxFrameLengthBytes)
    {
        var metrics = new ViewEngineMetrics();
        var store = new CollectionStore(metrics, new LiveViewEngineOptions { EagerIndexing = false });
        var engine = new ViewEngine(store, new TestPublisher(), NullLogger<ViewEngine>.Instance, metrics);
        var dispatcher = new TcpIngestRequestDispatcher(
            engine,
            store,
            new TcpIngestOptions
            {
                CollectionQueueCapacity = 1024,
                MaxFrameLengthBytes = maxFrameLengthBytes
            },
            new TestHostApplicationLifetime(),
            NullLogger<TcpIngestRequestDispatcher>.Instance);

        return new TcpIngestConnectionHandler(
            new TcpIngestOptions
            {
                CollectionQueueCapacity = 1024,
                MaxFrameLengthBytes = maxFrameLengthBytes
            },
            dispatcher,
            NullLogger<TcpIngestConnectionHandler>.Instance);
    }

    private sealed class TestPublisher : IOutboundPublisher
    {
        public ValueTask PublishAsync(
            IReadOnlyList<SubscriberTarget> targets,
            IReadOnlyList<ViewDelta> deltas,
            CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
