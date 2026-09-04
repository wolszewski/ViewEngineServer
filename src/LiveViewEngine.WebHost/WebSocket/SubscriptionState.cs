using LiveViewEngine.Core;

namespace ViewEngineServer.WebApp.WebSocket;

internal sealed class SubscriptionState(OutboundMessageFormat format)
{
    public OutboundMessageFormat Format { get; set; } = format;
    public bool IsSnapshotActive { get; set; }
    public List<ViewDelta> PendingLiveDeltas { get; } = [];
    public Queue<byte[]> BufferedFrames { get; } = new();

    // Diagnostics for the in-flight snapshot delivery: rows actually seen across SnapshotRowsDelta
    // batches vs. the totalCount announced by SnapshotStartDelta, compared at EndOfSnapshotDelta to
    // surface any row loss between computing the snapshot and finishing its delivery.
    public int SnapshotExpectedTotalCount { get; set; }
    public int SnapshotRowsSeen { get; set; }
    public bool SnapshotIsPartial { get; set; }
}
