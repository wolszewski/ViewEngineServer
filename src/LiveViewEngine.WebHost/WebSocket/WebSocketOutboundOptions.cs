namespace ViewEngineServer.WebApp.WebSocket;

public sealed class WebSocketOutboundOptions
{
    // Safety-net cap on buffered frames per connection - purely to bound worst-case memory if a
    // producer bug ever floods a connection (not sized to any expected collection/snapshot size).
    // The actual slow-client detector is SendStallTimeout below, which does not depend on how many
    // rows any particular subscription legitimately asked for: a full-collection snapshot writes
    // its frames into the queue almost instantly while the drain loop can only flush them one send
    // at a time, so even a perfectly healthy client will transiently queue up close to the total
    // row count during a big snapshot. Gating disconnect on queue size alone would force this
    // capacity to be set >= your largest expected snapshot, which defeats the point - hence this
    // defaults very high and should rarely if ever be the thing that actually trips.
    public int OutboundQueueCapacity { get; init; } = 2_000_000;

    // If a single outbound send takes longer than this to complete, the client is treated as
    // stalled (its TCP receive window isn't draining, or the connection is dead) and is aborted.
    // This is the real backpressure signal: it reflects whether the client is still making forward
    // progress, not how much data it asked for, so legitimately large full-collection snapshots
    // succeed regardless of size as long as the client keeps accepting sends.
    public TimeSpan SendStallTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
