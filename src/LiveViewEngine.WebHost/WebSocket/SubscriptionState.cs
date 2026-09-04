using LiveViewEngine.Core;

namespace ViewEngineServer.WebApp.WebSocket;

internal sealed class SubscriptionState(OutboundMessageFormat format)
{
    public OutboundMessageFormat Format { get; set; } = format;
    public bool IsSnapshotActive { get; set; }
    public List<ViewDelta> PendingLiveDeltas { get; } = [];
    public Queue<byte[]> BufferedFrames { get; } = new();

    // Diagnostics for the in-flight snapshot delivery: how many rows have streamed so far and
    // whether this is a partial (viewport-expansion) batch - logged at EndOfSnapshotDelta. Not
    // compared against the collection's totalCount here, because a bounded/paged subscription
    // legitimately streams fewer rows than totalCount without being "partial" (see
    // WebSocketOutboundPublisher.PublishDelta's EndOfSnapshotDelta case for why).
    public int SnapshotRowsSeen { get; set; }
    public bool SnapshotIsPartial { get; set; }
}
