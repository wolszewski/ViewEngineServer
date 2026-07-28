using ViewEngineServer.Core.Delta;

namespace ViewEngineServer.Core.Publishing;

/// <summary>
/// Transport-agnostic contract for delivering <see cref="DeltaEvent"/>s to a
/// specific connected client.
///
/// Implementations live in the adapter layer (WebSocket, TCP, …) and are the
/// only place where socket / stream types appear.
/// </summary>
public interface IOutboundPublisher
{
    /// <summary>
    /// Deliver one or more events to the client identified by
    /// <paramref name="connectionId"/>. Silently ignores unknown or
    /// already-disconnected connections.
    /// </summary>
    ValueTask PublishAsync(string connectionId, IReadOnlyList<DeltaEvent> events,
                           CancellationToken ct = default);
}
