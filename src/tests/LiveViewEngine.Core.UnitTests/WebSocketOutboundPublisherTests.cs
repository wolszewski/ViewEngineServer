using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ViewEngineServer.WebApp.WebSocket;

namespace LiveViewEngine.Core.UnitTests;

public class WebSocketOutboundPublisherTests
{
    [Fact]
    public async Task PublishAsync_BuffersCompactLiveUpdatesUntilEos()
    {
        var publisher = new WebSocketOutboundPublisher(NullLogger<WebSocketOutboundPublisher>.Instance);
        var socket = new CapturingWebSocket();
        publisher.Register(1, socket);
        publisher.ConfigureSubscription(1, 7, OutboundMessageFormat.Compact, snapshotActive: true);

        var schema = new LiveViewEngine.Core.Data.CollectionSchema("orders", ["customer", "amount"]);
        await publisher.PublishAsync(
            [new LiveViewEngine.Core.SubscriberTarget(1, 7)],
            [
                new LiveViewEngine.Core.SnapshotStartDelta
                {
                    ViewId = "1:7",
                    Schema = schema,
                    StartIndex = 0,
                    TotalCount = 1,
                    VisibleFieldIndexes = [0, 1, 2]
                },
                new LiveViewEngine.Core.SnapshotRowsDelta
                {
                    ViewId = "1:7",
                    Schema = schema,
                    StartRowNumber = 0,
                    VisibleFieldIndexes = [0, 1, 2],
                    Rows = [["o1", "Alice", "100"]]
                }
            ]);

        await publisher.PublishAsync(
            [new LiveViewEngine.Core.SubscriberTarget(1, 7)],
            [
                new LiveViewEngine.Core.RowUpdateDelta
                {
                    ViewId = "1:7",
                    Schema = schema,
                    RowId = "o1",
                    Position = 0,
                    VisibleFieldIndexes = [0, 1, 2],
                    ChangedColumns = [new KeyValuePair<int, string?>(2, "150")]
                }
            ]);

        await publisher.PublishAsync(
            [new LiveViewEngine.Core.SubscriberTarget(1, 7)],
            [
                new LiveViewEngine.Core.EndOfSnapshotDelta
                {
                    ViewId = "1:7"
                }
            ]);

        await socket.WaitForMessagesAsync(4);

        Assert.Equal(
            ["P|7|0|1|0|customer|amount", "S|7|0|o1|Alice|100", "EOS|7", "U|7|o1|0|^1|150"],
            socket.Messages.ToArray());
    }

    [Fact]
    public async Task PublishAsync_BuffersJsonLiveUpdatesUntilEos()
    {
        var publisher = new WebSocketOutboundPublisher(NullLogger<WebSocketOutboundPublisher>.Instance);
        var socket = new CapturingWebSocket();
        publisher.Register(1, socket);
        publisher.ConfigureSubscription(1, 7, OutboundMessageFormat.Json, snapshotActive: true);

        var schema = new LiveViewEngine.Core.Data.CollectionSchema("orders", ["customer", "amount"]);
        await publisher.PublishAsync(
            [new LiveViewEngine.Core.SubscriberTarget(1, 7)],
            [
                new LiveViewEngine.Core.SnapshotStartDelta
                {
                    ViewId = "1:7",
                    Schema = schema,
                    StartIndex = 0,
                    TotalCount = 1,
                    VisibleFieldIndexes = [0, 1, 2]
                },
                new LiveViewEngine.Core.SnapshotRowsDelta
                {
                    ViewId = "1:7",
                    Schema = schema,
                    StartRowNumber = 0,
                    VisibleFieldIndexes = [0, 1, 2],
                    Rows = [["o1", "Alice", "100"]]
                }
            ]);

        await publisher.PublishAsync(
            [new LiveViewEngine.Core.SubscriberTarget(1, 7)],
            [
                new LiveViewEngine.Core.RowUpdateDelta
                {
                    ViewId = "1:7",
                    Schema = schema,
                    RowId = "o1",
                    Position = 0,
                    VisibleFieldIndexes = [0, 1, 2],
                    ChangedColumns = [new KeyValuePair<int, string?>(2, "150")]
                }
            ]);

        await publisher.PublishAsync(
            [new LiveViewEngine.Core.SubscriberTarget(1, 7)],
            [
                new LiveViewEngine.Core.EndOfSnapshotDelta
                {
                    ViewId = "1:7"
                }
            ]);

        await socket.WaitForMessagesAsync(4);

        AssertJsonMessage(socket.Messages.ElementAt(0), "snapshotStart", 7, ("startIndex", "0"), ("totalCount", "1"), ("fields", "[\"customer\",\"amount\"]"));
        AssertJsonMessage(socket.Messages.ElementAt(1), "snapshotRow", 7, ("rowNumber", "0"), ("row", "{\"key\":\"o1\",\"customer\":\"Alice\",\"amount\":\"100\"}"));
        AssertJsonMessage(socket.Messages.ElementAt(2), "eos", 7);
        AssertJsonMessage(socket.Messages.ElementAt(3), "rowUpdate", 7, ("rowId", "\"o1\""), ("position", "0"), ("changedFields", "{\"amount\":\"150\"}"));
    }

    [Fact]
    public async Task PublishAsync_BatchesCompactSnapshotRowsIntoSingleWebSocketMessage()
    {
        var publisher = new WebSocketOutboundPublisher(NullLogger<WebSocketOutboundPublisher>.Instance);
        var socket = new CapturingWebSocket();
        publisher.Register(1, socket);
        publisher.ConfigureSubscription(1, 7, OutboundMessageFormat.Compact, snapshotActive: true);

        var schema = new LiveViewEngine.Core.Data.CollectionSchema("orders", ["customer", "amount"]);
        await publisher.PublishAsync(
            [new LiveViewEngine.Core.SubscriberTarget(1, 7)],
            [
                new LiveViewEngine.Core.SnapshotRowsDelta
                {
                    ViewId = "1:7",
                    Schema = schema,
                    StartRowNumber = 0,
                    VisibleFieldIndexes = [0, 1, 2],
                    Rows =
                    [
                        ["o1", "Alice", "100"],
                        ["o2", "Bob", "200"]
                    ]
                }
            ]);

        await socket.WaitForMessagesAsync(1);
        Assert.Equal("S|7|0|o1|Alice|100\nS|7|1|o2|Bob|200", Assert.Single(socket.Messages));
    }

    [Fact]
    public async Task PublishAsync_CoalescesConsecutiveRowUpdatesForSameRow()
    {
        var publisher = new WebSocketOutboundPublisher(NullLogger<WebSocketOutboundPublisher>.Instance);
        var socket = new CapturingWebSocket();
        publisher.Register(1, socket);
        publisher.ConfigureSubscription(1, 7, OutboundMessageFormat.Compact, snapshotActive: false);

        var schema = new LiveViewEngine.Core.Data.CollectionSchema("orders", ["customer", "amount"]);
        await publisher.PublishAsync(
            [new LiveViewEngine.Core.SubscriberTarget(1, 7)],
            [
                new LiveViewEngine.Core.RowUpdateDelta
                {
                    ViewId = "1:7",
                    Schema = schema,
                    RowId = "o1",
                    Position = 0,
                    VisibleFieldIndexes = [0, 1, 2],
                    ChangedColumns = [new KeyValuePair<int, string?>(2, "100")]
                },
                new LiveViewEngine.Core.RowUpdateDelta
                {
                    ViewId = "1:7",
                    Schema = schema,
                    RowId = "o1",
                    Position = 0,
                    VisibleFieldIndexes = [0, 1, 2],
                    ChangedColumns = [new KeyValuePair<int, string?>(2, "150")]
                }
            ]);

        await publisher.FlushAsync();
        await socket.WaitForMessagesAsync(1);

        var message = Assert.Single(socket.Messages);
        Assert.Contains("U|7|o1|0|", message);
        Assert.Contains("150", message);
        Assert.DoesNotContain("100", message);
    }

    [Fact]
    public async Task PublishAsync_DropsInsertRemovePairForSameRow()
    {
        var publisher = new WebSocketOutboundPublisher(NullLogger<WebSocketOutboundPublisher>.Instance);
        var socket = new CapturingWebSocket();
        publisher.Register(1, socket);
        publisher.ConfigureSubscription(1, 7, OutboundMessageFormat.Compact, snapshotActive: false);

        var schema = new Data.CollectionSchema("orders", ["customer", "amount"]);
        await publisher.PublishAsync(
            [new SubscriberTarget(1, 7)],
            [
                new RowInsertDelta
                {
                    ViewId = "1:7",
                    Schema = schema,
                    Position = 0,
                    VisibleFieldIndexes = [0, 1, 2],
                    Row = ["o1", "Alice", "100"]
                }
            ]);

        await publisher.PublishAsync(
            [new SubscriberTarget(1, 7)],
            [
                new RowRemoveDelta
                {
                    ViewId = "1:7",
                    RowId = "o1",
                    Position = 0
                }
            ]);

        Assert.Empty(socket.Messages);
    }

    [Fact]
    public async Task PublishAsync_WhenOutboundQueueFills_AbortsSlowConnectionInsteadOfBlocking()
    {
        var publisher = new WebSocketOutboundPublisher(NullLogger<WebSocketOutboundPublisher>.Instance);
        var socket = new BlockingWebSocket();
        publisher.Register(1, socket);
        publisher.ConfigureSubscription(1, 7, OutboundMessageFormat.Compact, snapshotActive: false);

        var schema = new Data.CollectionSchema("orders", ["customer", "amount"]);
        var deltas = Enumerable.Range(0, 600)
            .Select(i => (ViewDelta)new RowInsertDelta
            {
                ViewId = "1:7",
                Schema = schema,
                Position = i,
                VisibleFieldIndexes = [0, 1, 2],
                Row = [$"o{i}", $"Customer {i}", i.ToString()]
            })
            .ToArray();

        await publisher.PublishAsync([new SubscriberTarget(1, 7)], deltas).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await publisher.FlushAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(WebSocketState.Aborted, socket.State);
    }

    private static void AssertJsonMessage(string json, string type, int subscriptionId, params (string Name, string Value)[] properties)
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

    private sealed class CapturingWebSocket : WebSocket
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public async Task WaitForMessagesAsync(int expectedCount)
        {
            var timeoutAt = DateTime.UtcNow.AddSeconds(5);
            while (_messages.Count < expectedCount && DateTime.UtcNow < timeoutAt)
            {
                await Task.Delay(25);
            }
        }

        public override void Abort()
        {
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken)
        {
            _messages.Enqueue(Encoding.UTF8.GetString(buffer));
            return Task.CompletedTask;
        }

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType,
            WebSocketMessageFlags endOfMessage, CancellationToken cancellationToken)
        {
            _messages.Enqueue(Encoding.UTF8.GetString(buffer.Span));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingWebSocket : WebSocket
    {
        private readonly TaskCompletionSource _blockedSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            _blockedSend.TrySetException(new WebSocketException("aborted"));
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            _blockedSend.TrySetResult();
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            _blockedSend.TrySetResult();
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _blockedSend.TrySetResult();
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken) => _blockedSend.Task;

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType,
            WebSocketMessageFlags endOfMessage, CancellationToken cancellationToken) => new(_blockedSend.Task);
    }
}
