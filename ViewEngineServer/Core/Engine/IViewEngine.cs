using ViewEngineServer.Core.Delta;
using ViewEngineServer.Core.Ingestion;
using ViewEngineServer.Core.Subscriptions;

namespace ViewEngineServer.Core.Engine;

/// <summary>
/// Top-level transport-agnostic interface for the view engine.
///
/// All business logic lives behind this interface. Adapters (HTTP, TCP, WebSocket)
/// translate wire messages into <see cref="IngestCommand"/> /
/// <see cref="SubscriptionCommand"/> values and then call these two methods.
/// No adapter type ever leaks past this boundary.
/// </summary>
public interface IViewEngine
{
    /// <summary>
    /// Process a create-collection, upsert-row, or delete-row command.
    /// After storage is updated the engine computes delta events for every
    /// subscriber affected by the mutation and pushes them via
    /// <see cref="IOutboundPublisher"/>.
    /// </summary>
    Task<IngestResult> IngestAsync(IngestCommand command, CancellationToken ct = default);

    /// <summary>
    /// Process a subscribe, change-viewport, or unsubscribe command.
    /// Returns the events that must be delivered immediately to the requesting
    /// client (typically a <see cref="SnapshotEvent"/>). Subsequent delta events
    /// are pushed asynchronously via <see cref="IOutboundPublisher"/>.
    /// </summary>
    Task<IReadOnlyList<DeltaEvent>> SubscribeAsync(SubscriptionCommand command,
                                                    CancellationToken ct = default);
}
