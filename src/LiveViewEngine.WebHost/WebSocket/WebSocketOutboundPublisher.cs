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
                connection.WriteAsync(subscription.BufferedFrames.Dequeue()).AsTask().GetAwaiter().GetResult();
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
            connection.WriteAsync(message, ct).AsTask().GetAwaiter().GetResult();
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
                    PublishDeltaAsync(connection, subscription, target.SubscriptionId, delta, ct)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
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
                        _flushPolicy.FlushPendingLiveDeltasAsync(connection, subscriptionId, subscription, _encoders, ct)
                            .AsTask()
                            .GetAwaiter()
                            .GetResult();
                    }
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask PublishDeltaAsync(
        WebSocketConnection connection,
        SubscriptionState subscription,
        int subscriptionId,
        ViewDelta delta,
        CancellationToken ct)
    {
        try
        {
            var encoder = _encoders[subscription.Format];
            if (_flushPolicy.IsSnapshotControlOrData(delta))
            {
                await _flushPolicy.FlushPendingLiveDeltasAsync(connection, subscriptionId, subscription, _encoders, ct);

                foreach (var payload in encoder.EncodeFrames(delta, subscriptionId))
                {
                    await connection.WriteAsync(payload, ct);
                }

                if (delta is EndOfSnapshotDelta or SnapshotDelta)
                {
                    await _flushPolicy.CompleteSnapshotAsync(connection, subscription, ct);
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
                    await connection.WriteAsync(payload, ct);
                }
            }

            if (_flushPolicy.ShouldFlushPendingLiveDeltas(subscription))
            {
                await _flushPolicy.FlushPendingLiveDeltasAsync(connection, subscriptionId, subscription, _encoders, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to encode outbound delta for subscription '{SubscriptionId}'.", subscriptionId);
        }
    }

}
