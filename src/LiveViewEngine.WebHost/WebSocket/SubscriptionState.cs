using LiveViewEngine.Core;

namespace ViewEngineServer.WebApp.WebSocket;

internal sealed class SubscriptionState(OutboundMessageFormat format)
{
    public OutboundMessageFormat Format { get; set; } = format;
    public bool IsSnapshotActive { get; set; }
    public List<ViewDelta> PendingLiveDeltas { get; } = [];
    public Queue<byte[]> BufferedFrames { get; } = new();

    // Diagnostics for the in-flight snapshot stream: expected next row number for the next
    // SnapshotRowsDelta batch plus whether any gap/overlap has already been observed.
    public int SnapshotExpectedNextRowNumber { get; set; }
    public int SnapshotRowsSeen { get; set; }
    public bool SnapshotIsPartial { get; set; }
    public bool SnapshotHasRowNumberDiscontinuity { get; set; }
}
