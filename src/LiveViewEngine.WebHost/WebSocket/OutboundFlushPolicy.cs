using LiveViewEngine.Core;

namespace ViewEngineServer.WebApp.WebSocket;

internal sealed class OutboundFlushPolicy
{
    private const int MaxPendingLiveDeltasPerSubscription = 64;

    public bool ShouldFlushPendingLiveDeltas(SubscriptionState subscription) =>
        subscription.PendingLiveDeltas.Count >= MaxPendingLiveDeltasPerSubscription;

    public void FlushPendingLiveDeltas(
        WebSocketConnection connection,
        int subscriptionId,
        SubscriptionState subscription,
        IReadOnlyDictionary<OutboundMessageFormat, IOutboundProtocolEncoder> encoders)
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
                connection.TryWrite(payload);
            }
        }

        subscription.PendingLiveDeltas.Clear();
    }

    public void CompleteSnapshot(
        WebSocketConnection connection,
        SubscriptionState subscription)
    {
        subscription.IsSnapshotActive = false;
        while (subscription.BufferedFrames.Count > 0)
        {
            connection.TryWrite(subscription.BufferedFrames.Dequeue());
        }
    }

    public bool IsSnapshotControlOrData(ViewDelta delta) =>
        delta is SnapshotStartDelta or SnapshotRowsDelta or EndOfSnapshotDelta or SnapshotDelta;
}
