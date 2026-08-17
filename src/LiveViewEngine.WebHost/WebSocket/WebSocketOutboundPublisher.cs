using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using LiveViewEngine.Core;
using LiveViewEngine.Core.Output;

namespace ViewEngineServer.WebApp.WebSocket;

public sealed class WebSocketOutboundPublisher(
    IOutboundEventFormatter formatter,
    ILogger<WebSocketOutboundPublisher> logger) : IOutboundPublisher
{
    private readonly ConcurrentDictionary<int, ConnectionState> _connections = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Called when a WebSocket connection is accepted; starts a background drain consumer.
    public void Register(int connectionId, System.Net.WebSockets.WebSocket socket)
    {
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        var state = new ConnectionState(socket, channel);
        _connections[connectionId] = state;
        state.DrainTask = Task.Run(() => DrainAsync(connectionId, socket, channel.Reader));
    }

    public void Unregister(int connectionId)
    {
        if (_connections.TryRemove(connectionId, out var state))
        {
            state.Channel.Writer.TryComplete();
        }
    }

    // Serialize the deltas once per subscription id, then enqueue the bytes for each connection.
    // Returns immediately — slow clients drop stale frames, never block ingestion.
    public ValueTask PublishAsync(
        IReadOnlyList<SubscriberTarget> targets,
        IReadOnlyList<ViewDelta> deltas,
        CancellationToken ct = default)
    {
        foreach (var group in targets.GroupBy(static t => t.SubscriptionId))
        {
            byte[] payload;
            try
            {
                var events = formatter.Format(deltas, group.Key);
                payload = JsonSerializer.SerializeToUtf8Bytes(events, JsonOptions);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to serialise events for {Count} subscription target(s).", targets.Count);
                return ValueTask.CompletedTask;
            }

            foreach (var target in group)
            {
                if (_connections.TryGetValue(target.ConnectionId, out var state))
                {
                    state.Channel.Writer.TryWrite(payload);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask PublishSubscriptionAcceptedAsync(int connectionId, int subscriptionId, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(connectionId, out var state))
        {
            return ValueTask.CompletedTask;
        }

        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(
                new SubscriptionAcceptedMessage { SubscriptionId = subscriptionId },
                JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to serialise subscription acceptance for client '{ConnectionId}'.", connectionId);
            return ValueTask.CompletedTask;
        }

        state.Channel.Writer.TryWrite(payload);
        return ValueTask.CompletedTask;
    }

    private async Task DrainAsync(
        int connectionId,
        System.Net.WebSockets.WebSocket socket,
        ChannelReader<byte[]> reader)
    {
        await foreach (var payload in reader.ReadAllAsync())
        {
            if (socket.State != WebSocketState.Open)
            {
                break;
            }

            try
            {
                await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
            catch (WebSocketException ex)
            {
                logger.LogDebug(ex, "WebSocket send failed for client '{ConnectionId}'.", connectionId);
                break;
            }
        }
    }

    private sealed class ConnectionState(
        System.Net.WebSockets.WebSocket socket,
        Channel<byte[]> channel)
    {
        public readonly System.Net.WebSockets.WebSocket Socket = socket;
        public readonly Channel<byte[]> Channel = channel;
        public Task DrainTask = Task.CompletedTask;
    }

    private sealed class SubscriptionAcceptedMessage
    {
        public string Type { get; init; } = "subscriptionAccepted";
        public required int SubscriptionId { get; init; }
    }
}
