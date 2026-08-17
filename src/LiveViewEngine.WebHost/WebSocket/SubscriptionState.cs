using LiveViewEngine.Core;

namespace ViewEngineServer.WebApp.WebSocket;

internal sealed class SubscriptionState(OutboundMessageFormat format)
{
    public OutboundMessageFormat Format { get; set; } = format;
    public bool IsSnapshotActive { get; set; }
    public List<ViewDelta> PendingLiveDeltas { get; } = [];
    public Queue<byte[]> BufferedFrames { get; } = new();
}
