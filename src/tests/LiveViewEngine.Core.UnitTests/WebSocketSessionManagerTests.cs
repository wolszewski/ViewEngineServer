using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.DataIngest;
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
    public async Task HandleConnectionAsync_UpdateViewWithProjectedFields_SnapshotModeFull_EmitsSnapshotMetadata()
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
            Schema = new CollectionSchema("trades", ["symbol", "status", "amount"],
                [ScalarFieldType.String, ScalarFieldType.String, ScalarFieldType.Decimal])
        });
        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "trades",
            Key = "t1",
            Fields = new Dictionary<string, string?>
            {
                ["symbol"] = "AAPL",
                ["status"] = "open",
                ["amount"] = "100"
            }
        });

        var socket = new ScriptedWebSocket([
            "{\"type\":\"subscribe\",\"collectionId\":\"trades\",\"fields\":[\"symbol\",\"status\"],\"startIndex\":0,\"pageSize\":50,\"sendSnapshot\":true,\"messageFormat\":\"compact\"}",
            "{\"type\":\"updateview\",\"subscriptionId\":1,\"startIndex\":0,\"pageSize\":50,\"fields\":[\"status\",\"amount\"],\"messageFormat\":\"compact\",\"snapshotMode\":\"full\"}"
        ], closeAfterSentCount: 6);

        var handleTask = manager.HandleConnectionAsync(socket, CancellationToken.None);
        await socket.WaitForMessageTypeAsync("eos");
        socket.Close();
        await handleTask;

        var snapshotStarts = socket.SentMessages
            .Where(message => message.StartsWith("P|", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal("P|1|0|1|status|amount", snapshotStarts[^1]);
    }

    [Fact]
    public async Task HandleConnectionAsync_UpdateViewViewportExpansionWithoutSendSnapshot_SendsOnlyMissingCompactRows()
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
            Schema = new CollectionSchema("trades", ["tradeId"], [ScalarFieldType.String])
        });

        for (int i = 0; i < 500; i++)
        {
            await engine.IngestAsync(new UpsertRowCommand
            {
                CollectionId = "trades",
                Key = $"t{i:D3}",
                Fields = new Dictionary<string, string?> { ["tradeId"] = i.ToString("D3") }
            });
        }

        var socket = new ScriptedWebSocket(
            [
                "{\"type\":\"subscribe\",\"collectionId\":\"trades\",\"sortColumn\":\"tradeId\",\"sortAscending\":true,\"startIndex\":0,\"pageSize\":200,\"sendSnapshot\":true,\"messageFormat\":\"compact\",\"filters\":[]}",
                "{\"type\":\"updateview\",\"subscriptionId\":1,\"startIndex\":0,\"pageSize\":400,\"sortColumn\":\"tradeId\",\"sortAscending\":true,\"filters\":[],\"fields\":[],\"snapshotMode\":\"delta\"}"
            ],
            closeAfterSentCount: 204);

        var handleTask = manager.HandleConnectionAsync(socket, CancellationToken.None);
        await socket.WaitForMessagesAsync(204);
        socket.Close();
        await handleTask;

        var allMessages = socket.SentMessages.ToArray();

        var snapshotStarts = allMessages
            .Where(static message => message.StartsWith("P|", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(snapshotStarts);
        Assert.Equal("P|1|200|500|1|tradeId", snapshotStarts[0]);

        var snapshotStartIndex = Array.IndexOf(allMessages, snapshotStarts[0]);
        var eosIndex = Array.FindIndex(allMessages, snapshotStartIndex + 1,
            static message => message.StartsWith("EOS|", StringComparison.Ordinal));
        Assert.True(snapshotStartIndex >= 0);
        Assert.True(eosIndex > snapshotStartIndex);

        var updateSnapshotMessages = allMessages[(snapshotStartIndex + 1)..eosIndex];
        var snapshotRows = updateSnapshotMessages
            .Where(static message => message.StartsWith("S|", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(200, snapshotRows.Length);
        Assert.Equal("S|1|200|t200|200", snapshotRows[0]);
        Assert.Equal("S|1|399|t399|399", snapshotRows[^1]);

        var insertFrames = updateSnapshotMessages
            .Where(static message => message.StartsWith("I|", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(insertFrames);

        var eosFrames = allMessages
            .Where(static message => message.StartsWith("EOS|", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, eosFrames.Length);
    }

    [Fact]
    public async Task HandleConnectionAsync_UpdateViewViewportExpansionSnapshotModeNo_DoesNotEmitSnapshot()
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
            Schema = new CollectionSchema("trades", ["tradeId"], [ScalarFieldType.String])
        });

        for (int i = 0; i < 500; i++)
        {
            await engine.IngestAsync(new UpsertRowCommand
            {
                CollectionId = "trades",
                Key = $"t{i:D3}",
                Fields = new Dictionary<string, string?> { ["tradeId"] = i.ToString("D3") }
            });
        }

        var socket = new ScriptedWebSocket(
            [
                "{\"type\":\"subscribe\",\"collectionId\":\"trades\",\"sortColumn\":\"tradeId\",\"sortAscending\":true,\"startIndex\":0,\"pageSize\":200,\"sendSnapshot\":true,\"messageFormat\":\"compact\",\"filters\":[]}",
                "{\"type\":\"updateview\",\"subscriptionId\":1,\"startIndex\":0,\"pageSize\":400,\"sortColumn\":\"tradeId\",\"sortAscending\":true,\"filters\":[],\"fields\":[],\"snapshotMode\":\"no\"}"
            ],
            closeAfterSentCount: 1);

        var handleTask = manager.HandleConnectionAsync(socket, CancellationToken.None);
        await socket.WaitForMessagesAsync(1);
        socket.Close();
        await handleTask;

        var updateMessages = socket.SentMessages.SkipWhile(static m => !m.StartsWith("EOS|", StringComparison.Ordinal)).Skip(1).ToArray();
        Assert.Empty(updateMessages);
    }

    [Fact]
    public async Task HandleConnectionAsync_NewSubscribeIgnoresClientAssignedIdAndAllocatesUniqueId()
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
            Schema = new CollectionSchema("trades", ["instrument"], [ScalarFieldType.String])
        });
        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "trades",
            Key = "t1",
            Fields = new Dictionary<string, string?> { ["instrument"] = "AAPL" }
        });

        var socket = new ScriptedWebSocket([
            "{\"type\":\"subscribe\",\"subscriptionId\":1,\"collectionId\":\"trades\",\"startIndex\":0,\"pageSize\":50,\"sendSnapshot\":true,\"messageFormat\":\"json\"}",
            "{\"type\":\"subscribe\",\"collectionId\":\"trades\",\"startIndex\":0,\"pageSize\":50,\"sendSnapshot\":true,\"messageFormat\":\"json\"}"
        ], closeAfterSentCount: 5);

        var handleTask = manager.HandleConnectionAsync(socket, CancellationToken.None);
        await socket.WaitForMessagesAsync(5);
        socket.Close();
        await handleTask;

        var acceptedSubscriptionIds = socket.SentMessages
            .Select(static message => JsonDocument.Parse(message).RootElement)
            .Where(static root => root.GetProperty("type").GetString() == "subscriptionAccepted")
            .Select(static root => root.GetProperty("subscriptionId").GetInt32())
            .ToArray();

        Assert.Equal([1, 2], acceptedSubscriptionIds);
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
    public async Task HandleConnectionAsync_SetViewportActivatesBufferBeforeDispatch()
    {
        var publisher = new WebSocketOutboundPublisher(NullLogger<WebSocketOutboundPublisher>.Instance);
        var engine = new BlockingViewEngine();
        var manager = new WebSocketSessionManager(
            engine,
            new CollectionStore(new ViewEngineMetrics(), new LiveViewEngineOptions { EagerIndexing = false }),
            publisher,
            NullLogger<WebSocketSessionManager>.Instance);

        var socket = new ScriptedWebSocket([
            "{\"type\":\"subscribe\",\"collectionId\":\"trades\",\"startIndex\":0,\"pageSize\":50,\"sendSnapshot\":true,\"messageFormat\":\"json\"}",
            "{\"type\":\"setViewport\",\"subscriptionId\":1,\"startIndex\":0,\"pageSize\":50,\"messageFormat\":\"json\"}"
        ]);

        var handleTask = manager.HandleConnectionAsync(socket, CancellationToken.None);
        await engine.WaitForSetViewportAsync();

        Assert.True(ReadSnapshotActive(publisher, 1, 1));

        engine.ReleaseSetViewport();
        socket.Close();
        await handleTask;
    }

    private static bool ReadSnapshotActive(WebSocketOutboundPublisher publisher, int connectionId, int subscriptionId)
    {
        var connectionsField = typeof(WebSocketOutboundPublisher)
            .GetField("_connections", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var connections = (System.Collections.IDictionary?)connectionsField?.GetValue(publisher);
        var connection = connections?[connectionId];
        var subscriptionsProperty = connection?.GetType().GetProperty("Subscriptions");
        var subscriptions = (System.Collections.IDictionary?)subscriptionsProperty?.GetValue(connection);
        var subscription = subscriptions?[subscriptionId];
        var isSnapshotActiveProperty = subscription?.GetType().GetProperty("IsSnapshotActive");
        return isSnapshotActiveProperty is not null && (bool)isSnapshotActiveProperty.GetValue(subscription)!;
    }

    private sealed class BlockingViewEngine : IViewEngine
    {
        private readonly TaskCompletionSource _setViewportStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _setViewportRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<ViewDelta>> SubscribeAsync(SubscriptionCommand command, CancellationToken ct = default)
        {
            if (command is UpdateViewCommand)
            {
                _setViewportStarted.TrySetResult();
                await _setViewportRelease.Task.WaitAsync(ct);
                return [];
            }

            return [];
        }

        public Task<IngestResult> IngestAsync(IngestCommand command, CancellationToken ct = default) =>
            Task.FromResult(IngestResult.Ok());

        public Task WaitForSetViewportAsync() => _setViewportStarted.Task;

        public void ReleaseSetViewport() => _setViewportRelease.TrySetResult();
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
