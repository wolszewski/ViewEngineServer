using System.Collections.Concurrent;
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

    private readonly OutboundFlushPolicy _flushPolicy = new();
    private readonly ConcurrentDictionary<int, WebSocketConnection> _connections = new();

    public void Register(int connectionId, System.Net.WebSockets.WebSocket socket)
    {
        var connection = new WebSocketConnection(connectionId, socket, logger);
        _connections[connectionId] = connection;
        connection.StartDrain();
    }

    public void Unregister(int connectionId)
    {
        if (_connections.TryRemove(connectionId, out var connection))
        {
            lock (connection.Gate)
            {
                connection.Subscriptions.Clear();
            }

            connection.Complete();
        }
    }

    public void ConfigureSubscription(
        int connectionId,
        int subscriptionId,
        OutboundMessageFormat format,
        bool snapshotActive)
    {
        if (!_connections.TryGetValue(connectionId, out var connection))
        {
            return;
        }

        lock (connection.Gate)
        {
            if (!connection.Subscriptions.TryGetValue(subscriptionId, out var subscription))
            {
                subscription = new SubscriptionState(format);
                connection.Subscriptions[subscriptionId] = subscription;
            }

            subscription.Format = format;
            subscription.IsSnapshotActive = snapshotActive;
            subscription.PendingLiveDeltas.Clear();
            subscription.BufferedFrames.Clear();
        }
    }

    public void RemoveSubscription(int connectionId, int subscriptionId)
    {
        if (!_connections.TryGetValue(connectionId, out var connection))
        {
            return;
        }

        lock (connection.Gate)
        {
            if (connection.Subscriptions.Remove(subscriptionId, out var subscription))
            {
                subscription.PendingLiveDeltas.Clear();
                subscription.BufferedFrames.Clear();
            }
        }
    }

    public void BeginViewportSnapshot(int connectionId, int subscriptionId)
    {
        if (!_connections.TryGetValue(connectionId, out var connection))
        {
            return;
        }

        lock (connection.Gate)
        {
            if (!connection.Subscriptions.TryGetValue(subscriptionId, out var subscription))
            {
                return;
            }

            subscription.IsSnapshotActive = true;
            subscription.BufferedFrames.Clear();
        }
    }

    public void CancelSnapshot(int connectionId, int subscriptionId)
    {
        if (!_connections.TryGetValue(connectionId, out var connection))
        {
            return;
        }

        lock (connection.Gate)
        {
            if (!connection.Subscriptions.TryGetValue(subscriptionId, out var subscription))
            {
                return;
            }

            subscription.IsSnapshotActive = false;
            while (subscription.BufferedFrames.Count > 0)
            {
                connection.TryWrite(subscription.BufferedFrames.Dequeue());
            }
        }
    }

    public void SetSnapshotActive(int connectionId, int subscriptionId, bool snapshotActive)
    {
        if (!_connections.TryGetValue(connectionId, out var connection))
        {
            return;
        }

        lock (connection.Gate)
        {
            if (!connection.Subscriptions.TryGetValue(subscriptionId, out var subscription))
            {
                return;
            }

            subscription.IsSnapshotActive = snapshotActive;
            subscription.PendingLiveDeltas.Clear();
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
        if (!_connections.TryGetValue(connectionId, out var connection))
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

        lock (connection.Gate)
        {
            connection.TryWrite(message);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask PublishSubscriptionRejectedAsync(
        int connectionId,
        OutboundMessageFormat format,
        SubscriptionRejectedPayload payload,
        CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(connectionId, out var connection))
        {
            return ValueTask.CompletedTask;
        }

        byte[] message;
        try
        {
            message = _encoders[format].EncodeSubscriptionRejected(payload);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to encode subscription rejection for client '{ConnectionId}'.", connectionId);
            return ValueTask.CompletedTask;
        }

        lock (connection.Gate)
        {
            connection.TryWrite(message);
        }

        return ValueTask.CompletedTask;
    }

    // Non-terminal counterpart to PublishSubscriptionRejectedAsync - used when an
    // updateview/setviewport request is rejected (e.g. a disabled capability) but the subscription
    // itself stays alive with its previous view untouched. Deliberately does not touch
    // SubscriptionState (IsSnapshotActive/PendingLiveDeltas/BufferedFrames) - the caller
    // (WebSocketSessionManager) is responsible for reconciling any snapshot buffer state that was
    // already started before the rejection was known.
    public ValueTask PublishUpdateRejectedAsync(
        int connectionId,
        OutboundMessageFormat format,
        SubscriptionRejectedPayload payload,
        CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(connectionId, out var connection))
        {
            return ValueTask.CompletedTask;
        }

        byte[] message;
        try
        {
            message = _encoders[format].EncodeUpdateRejected(payload);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to encode update rejection for client '{ConnectionId}'.", connectionId);
            return ValueTask.CompletedTask;
        }

        lock (connection.Gate)
        {
            connection.TryWrite(message);
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
            if (!_connections.TryGetValue(target.ConnectionId, out var connection))
            {
                continue;
            }

            lock (connection.Gate)
            {
                if (!connection.Subscriptions.TryGetValue(target.SubscriptionId, out var subscription))
                {
                    subscription = new SubscriptionState(OutboundMessageFormat.Compact);
                    connection.Subscriptions[target.SubscriptionId] = subscription;
                }

                foreach (var delta in deltas)
                {
                    PublishDelta(connection, subscription, target.SubscriptionId, delta);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken ct = default)
    {
        foreach (var connection in _connections.Values)
        {
            lock (connection.Gate)
            {
                foreach (var (subscriptionId, subscription) in connection.Subscriptions)
                {
                    if (!subscription.IsSnapshotActive)
                    {
                        _flushPolicy.FlushPendingLiveDeltas(connection, subscriptionId, subscription, _encoders);
                    }
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    private void PublishDelta(
        WebSocketConnection connection,
        SubscriptionState subscription,
        int subscriptionId,
        ViewDelta delta)
    {
        try
        {
            var encoder = _encoders[subscription.Format];
            if (_flushPolicy.IsSnapshotControlOrData(delta))
            {
                _flushPolicy.FlushPendingLiveDeltas(connection, subscriptionId, subscription, _encoders);

                foreach (var payload in encoder.EncodeFrames(delta, subscriptionId))
                {
                    connection.TryWrite(payload);
                }

                if (delta is EndOfSnapshotDelta or SnapshotDelta)
                {
                    _flushPolicy.CompleteSnapshot(connection, subscription);
                }

                return;
            }

            if (subscription.IsSnapshotActive)
            {
                foreach (var payload in encoder.EncodeFrames(delta, subscriptionId))
                {
                    subscription.BufferedFrames.Enqueue(payload);
                }

                return;
            }

            if (!LiveDeltaCoalescer.TryQueueCoalescedDelta(subscription, delta))
            {
                foreach (var payload in encoder.EncodeFrames(delta, subscriptionId))
                {
                    connection.TryWrite(payload);
                }
            }

            if (_flushPolicy.ShouldFlushPendingLiveDeltas(subscription))
            {
                _flushPolicy.FlushPendingLiveDeltas(connection, subscriptionId, subscription, _encoders);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to encode outbound delta for subscription '{SubscriptionId}'.", subscriptionId);
        }
    }

}
