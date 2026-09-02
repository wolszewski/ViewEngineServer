using LiveViewEngine.Core;

namespace ViewEngineServer.WebApp.WebSocket;

internal sealed class OutboundFlushPolicy
{
    private const int MaxPendingLiveDeltasPerSubscription = 64;

    public bool ShouldFlushPendingLiveDeltas(SubscriptionState subscription) =>
        subscription.PendingLiveDeltas.Count >= MaxPendingLiveDeltasPerSubscription;

    public async ValueTask FlushPendingLiveDeltasAsync(
        WebSocketConnection connection,
        int subscriptionId,
        SubscriptionState subscription,
        IReadOnlyDictionary<OutboundMessageFormat, IOutboundProtocolEncoder> encoders,
        CancellationToken ct = default)
    {
        if (subscription.PendingLiveDeltas.Count == 0)
        {
            return;
        }

        var encoder = encoders[subscription.Format];
        foreach (var delta in subscription.PendingLiveDeltas)
        {
            foreach (var payload in encoder.EncodeFrames(delta, subscriptionId))
            {
                await connection.WriteAsync(payload, ct);
            }
        }

        subscription.PendingLiveDeltas.Clear();
    }

    public async ValueTask CompleteSnapshotAsync(
        WebSocketConnection connection,
        SubscriptionState subscription,
        CancellationToken ct = default)
    {
        subscription.IsSnapshotActive = false;
        while (subscription.BufferedFrames.Count > 0)
        {
            await connection.WriteAsync(subscription.BufferedFrames.Dequeue(), ct);
        }
    }

    public bool IsSnapshotControlOrData(ViewDelta delta) =>
        delta is SnapshotStartDelta or SnapshotRowsDelta or EndOfSnapshotDelta or SnapshotDelta;
}
