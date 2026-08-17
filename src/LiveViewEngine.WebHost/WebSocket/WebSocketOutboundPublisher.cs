using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using LiveViewEngine.Core;

namespace ViewEngineServer.WebApp.WebSocket;

public sealed class WebSocketOutboundPublisher(
    ILogger<WebSocketOutboundPublisher> logger) : IOutboundPublisher
{
    private readonly IReadOnlyDictionary<OutboundMessageFormat, IOutboundProtocolEncoder> _encoders =
        new Dictionary<OutboundMessageFormat, IOutboundProtocolEncoder>
        {
            [OutboundMessageFormat.Compact] = new CompactOutboundProtocolEncoder(),
            [OutboundMessageFormat.Json] = new JsonOutboundProtocolEncoder()
        };

    private readonly ConcurrentDictionary<int, ConnectionState> _connections = new();

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
            lock (state.Gate)
            {
                state.Subscriptions.Clear();
            }

            state.Channel.Writer.TryComplete();
        }
    }

    public void ConfigureSubscription(
        int connectionId,
        int subscriptionId,
        OutboundMessageFormat format,
        bool snapshotActive)
    {
        if (!_connections.TryGetValue(connectionId, out var state))
        {
            return;
        }

        lock (state.Gate)
        {
            if (!state.Subscriptions.TryGetValue(subscriptionId, out var subscription))
            {
                subscription = new SubscriptionState(format);
                state.Subscriptions[subscriptionId] = subscription;
            }

            subscription.Format = format;
            subscription.IsSnapshotActive = snapshotActive;
            subscription.BufferedFrames.Clear();
        }
    }

    public void RemoveSubscription(int connectionId, int subscriptionId)
    {
        if (!_connections.TryGetValue(connectionId, out var state))
        {
            return;
        }

        lock (state.Gate)
        {
            state.Subscriptions.Remove(subscriptionId);
        }
    }

    public void SetSnapshotActive(int connectionId, int subscriptionId, bool snapshotActive)
    {
        if (!_connections.TryGetValue(connectionId, out var state))
        {
            return;
        }

        lock (state.Gate)
        {
            if (!state.Subscriptions.TryGetValue(subscriptionId, out var subscription))
            {
                return;
            }

            subscription.IsSnapshotActive = snapshotActive;
            if (snapshotActive)
            {
                subscription.BufferedFrames.Clear();
            }
        }
    }

    public ValueTask PublishSubscriptionAcceptedAsync(
        int connectionId,
        OutboundMessageFormat format,
        SubscriptionAcceptedPayload payload,
        CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(connectionId, out var state))
        {
            return ValueTask.CompletedTask;
        }

        byte[] message;
        try
        {
            message = _encoders[format].EncodeSubscriptionAccepted(payload);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to encode subscription acceptance for client '{ConnectionId}'.", connectionId);
            return ValueTask.CompletedTask;
        }

        lock (state.Gate)
        {
            TryWrite(state, message);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask PublishAsync(
        IReadOnlyList<SubscriberTarget> targets,
        IReadOnlyList<ViewDelta> deltas,
        CancellationToken ct = default)
    {
        foreach (var target in targets)
        {
            if (!_connections.TryGetValue(target.ConnectionId, out var state))
            {
                continue;
            }

            lock (state.Gate)
            {
                if (!state.Subscriptions.TryGetValue(target.SubscriptionId, out var subscription))
                {
                    subscription = new SubscriptionState(OutboundMessageFormat.Compact);
                    state.Subscriptions[target.SubscriptionId] = subscription;
                }

                foreach (var delta in deltas)
                {
                    PublishDelta(state, subscription, target.SubscriptionId, delta);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    private void PublishDelta(
        ConnectionState state,
        SubscriptionState subscription,
        int subscriptionId,
        ViewDelta delta)
    {
        try
        {
            var encoder = _encoders[subscription.Format];
            if (IsSnapshotControlOrData(delta))
            {
                foreach (var payload in encoder.EncodeFrames(delta, subscriptionId))
                {
                    TryWrite(state, payload);
                }

                if (delta is EndOfSnapshotDelta or SnapshotDelta)
                {
                    CompleteSnapshot(state, subscriptionId, subscription);
                }

                return;
            }

            foreach (var payload in encoder.EncodeFrames(delta, subscriptionId))
            {
                if (subscription.IsSnapshotActive)
                {
                    subscription.BufferedFrames.Enqueue(payload);
                    continue;
                }

                TryWrite(state, payload);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to encode outbound delta for subscription '{SubscriptionId}'.", subscriptionId);
        }
    }

    private static bool IsSnapshotControlOrData(ViewDelta delta) =>
        delta is SnapshotStartDelta or SnapshotRowsDelta or EndOfSnapshotDelta or SnapshotDelta;

    private void CompleteSnapshot(ConnectionState state, int subscriptionId, SubscriptionState subscription)
    {
        subscription.IsSnapshotActive = false;
        while (subscription.BufferedFrames.Count > 0)
        {
            TryWrite(state, subscription.BufferedFrames.Dequeue());
        }
    }

    private void TryWrite(ConnectionState state, byte[] payload)
    {
        if (!state.Channel.Writer.TryWrite(payload))
        {
            logger.LogDebug("Dropping outbound frame for a slow WebSocket client.");
        }
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
        public readonly Lock Gate = new();
        public readonly Dictionary<int, SubscriptionState> Subscriptions = [];
        public readonly System.Net.WebSockets.WebSocket Socket = socket;
        public readonly Channel<byte[]> Channel = channel;
        public Task DrainTask = Task.CompletedTask;
    }

    private sealed class SubscriptionState(OutboundMessageFormat format)
    {
        public OutboundMessageFormat Format { get; set; } = format;
        public bool IsSnapshotActive { get; set; }
        public Queue<byte[]> BufferedFrames { get; } = new();
    }
}
