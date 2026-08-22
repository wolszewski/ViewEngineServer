using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;
using Microsoft.Extensions.Logging.Abstractions;
using ViewEngineServer.WebApp.WebSocket;

namespace LiveViewEngine.Core.UnitTests;

public class WebSocketSessionManagerTests
{
    [Fact]
    public async Task HandleConnectionAsync_SubscribeBeforeCollectionExists_ReportsSnapshotPending()
    {
        var metrics = new ViewEngineMetrics();
        var store = new CollectionStore(metrics, new LiveViewEngineOptions { EagerIndexing = false });
        var publisher = new WebSocketOutboundPublisher(NullLogger<WebSocketOutboundPublisher>.Instance);
        var engine = new ViewEngine(store, publisher, NullLogger<ViewEngine>.Instance, metrics);
        var manager = new WebSocketSessionManager(
            engine,
            store,
            publisher,
            NullLogger<WebSocketSessionManager>.Instance);
        var socket = new ScriptedWebSocket([
            "{\"type\":\"subscribe\",\"collectionId\":\"trades\",\"startIndex\":0,\"pageSize\":50,\"sendSnapshot\":true,\"messageFormat\":\"json\"}"
        ]);

        await manager.HandleConnectionAsync(socket, CancellationToken.None);
        await socket.WaitForMessagesAsync(1);

        var accepted = socket.SentMessages
            .Select(static message => JsonDocument.Parse(message))
            .Select(static document => document.RootElement)
            .First(static root => root.GetProperty("type").GetString() == "subscriptionAccepted");
        Assert.True(accepted.GetProperty("snapshotFollows").GetBoolean());
    }

    [Fact]
    public async Task HandleConnectionAsync_SetViewportNoNewArea_LiveDeltasNotBlocked()
    {
        var metrics = new ViewEngineMetrics();
        var store = new CollectionStore(metrics, new LiveViewEngineOptions { EagerIndexing = false });
        var publisher = new WebSocketOutboundPublisher(NullLogger<WebSocketOutboundPublisher>.Instance);
        var engine = new ViewEngine(store, publisher, NullLogger<ViewEngine>.Instance, metrics);
        var manager = new WebSocketSessionManager(
            engine,
            store,
            publisher,
            NullLogger<WebSocketSessionManager>.Instance);

        await engine.IngestAsync(new CreateCollectionCommand
        {
            CollectionId = "trades",
            Schema = new CollectionSchema("trades", ["instrument"],
                [ScalarFieldType.String])
        });
        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "trades",
            Key = "t1",
            Fields = new Dictionary<string, string?> { ["instrument"] = "AAPL" }
        });

        // subscribe (pageSize=50), then setViewport to the same range (contained → engine returns [])
        // then a live ingest should still produce a rowInsert
        var socket = new ScriptedWebSocket(
            [
                "{\"type\":\"subscribe\",\"collectionId\":\"trades\",\"startIndex\":0,\"pageSize\":50,\"sendSnapshot\":true,\"messageFormat\":\"json\"}",
                "{\"type\":\"setViewport\",\"subscriptionId\":1,\"startIndex\":0,\"pageSize\":50}"
            ],
            closeAfterSentCount: 5); // wait until eos + at least one rowInsert arrives

        var handleTask = manager.HandleConnectionAsync(socket, CancellationToken.None);

        // wait for eos (end-of-snapshot), meaning subscribe snapshot is complete
        await socket.WaitForMessageTypeAsync("eos");

        // ingest a new row → should produce a live rowInsert if buffering is not stuck
        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "trades",
            Key = "t2",
            Fields = new Dictionary<string, string?> { ["instrument"] = "MSFT" }
        });

        await socket.WaitForMessageTypeAsync("rowInsert");
        socket.Close();
        await handleTask;

        var rowInserts = socket.SentMessages
            .Select(static m => JsonDocument.Parse(m).RootElement)
            .Where(static el => el.GetProperty("type").GetString() == "rowInsert")
            .ToList();
        Assert.NotEmpty(rowInserts);
    }

    [Fact]
    public async Task HandleConnectionAsync_SubscribeWithFieldPresetId_AppliesFilterPresetFilter()
    {
        var metrics = new ViewEngineMetrics();
        var store = new CollectionStore(metrics, new LiveViewEngineOptions { EagerIndexing = false });
        var publisher = new WebSocketOutboundPublisher(NullLogger<WebSocketOutboundPublisher>.Instance);
        var engine = new ViewEngine(store, publisher, NullLogger<ViewEngine>.Instance, metrics);
        var manager = new WebSocketSessionManager(
            engine,
            store,
            publisher,
            NullLogger<WebSocketSessionManager>.Instance);

        await engine.IngestAsync(new CreateCollectionCommand
        {
            CollectionId = "trades",
            Schema = new CollectionSchema("trades", ["instrument", "status", "amount", "valueDate"],
                [ScalarFieldType.String, ScalarFieldType.String, ScalarFieldType.Decimal, ScalarFieldType.DateOnly])
        });
        await engine.IngestAsync(new CreateFilterPresetCommand
        {
            CollectionId = "trades",
            FilterPresetId = "today",
            Filters = [new FilterSpec("valueDate", FilterOperator.Eq, "2024-01-15")]
        });
        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "trades",
            Key = "t1",
            Fields = new Dictionary<string, string?>
            {
                ["instrument"] = "AAPL",
                ["status"] = "open",
                ["amount"] = "1000",
                ["valueDate"] = "2024-01-15"
            }
        });
        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "trades",
            Key = "t2",
            Fields = new Dictionary<string, string?>
            {
                ["instrument"] = "MSFT",
                ["status"] = "open",
                ["amount"] = "2000",
                ["valueDate"] = "2024-01-16"
            }
        });

        var socket = new ScriptedWebSocket([
            "{\"type\":\"subscribe\",\"collectionId\":\"trades\",\"fieldPresetId\":\"today\",\"startIndex\":0,\"pageSize\":50,\"sendSnapshot\":true,\"messageFormat\":\"json\"}"
        ]);

        await manager.HandleConnectionAsync(socket, CancellationToken.None);
        await socket.WaitForMessagesAsync(1);

        var accepted = socket.SentMessages
            .Select(static message => JsonDocument.Parse(message))
            .Select(static document => document.RootElement)
            .First(static root => root.GetProperty("type").GetString() == "subscriptionAccepted");

        Assert.Equal(1, accepted.GetProperty("totalCount").GetInt32());
    }

    private sealed class ScriptedWebSocket : WebSocket
    {
        private readonly Queue<string> _inboundMessages;
        private readonly List<string> _sentMessages = [];
        private readonly int _closeAfterSentCount;
        private WebSocketState _state = WebSocketState.Open;

        public ScriptedWebSocket(IEnumerable<string> inboundMessages, int closeAfterSentCount = 1)
        {
            _inboundMessages = new Queue<string>(inboundMessages);
            _closeAfterSentCount = closeAfterSentCount;
        }

        public IReadOnlyList<string> SentMessages
        {
            get
            {
                lock (_sentMessages)
                {
                    return _sentMessages.ToArray();
                }
            }
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void Close() => _state = WebSocketState.CloseReceived;

        public async Task WaitForMessagesAsync(int expectedCount)
        {
            var timeoutAt = DateTime.UtcNow.AddSeconds(5);
            while (SentMessages.Count < expectedCount && DateTime.UtcNow < timeoutAt)
            {
                await Task.Delay(25);
            }
        }

        public async Task WaitForMessageTypeAsync(string type)
        {
            var timeoutAt = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < timeoutAt)
            {
                if (SentMessages.Any(m =>
                {
                    try { return JsonDocument.Parse(m).RootElement.GetProperty("type").GetString() == type; }
                    catch { return false; }
                }))
                {
                    return;
                }

                await Task.Delay(25);
            }
        }

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (_inboundMessages.Count == 0)
            {
                var timeoutAt = DateTime.UtcNow.AddSeconds(5);
                while (SentMessages.Count < _closeAfterSentCount
                    && _state != WebSocketState.CloseReceived
                    && DateTime.UtcNow < timeoutAt)
                {
                    await Task.Delay(10, cancellationToken);
                }

                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            var payload = Encoding.UTF8.GetBytes(_inboundMessages.Dequeue());
            Array.Copy(payload, 0, buffer.Array!, buffer.Offset, payload.Length);
            return new WebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, true);
        }

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            if (messageType == WebSocketMessageType.Text)
            {
                lock (_sentMessages)
                {
                    _sentMessages.Add(Encoding.UTF8.GetString(buffer));
                }
            }

            return Task.CompletedTask;
        }

        public override ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            WebSocketMessageFlags endOfMessage,
            CancellationToken cancellationToken)
        {
            if (messageType == WebSocketMessageType.Text)
            {
                lock (_sentMessages)
                {
                    _sentMessages.Add(Encoding.UTF8.GetString(buffer.Span));
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
