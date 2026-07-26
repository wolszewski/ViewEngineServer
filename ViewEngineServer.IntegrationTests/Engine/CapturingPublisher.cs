using ViewEngineServer.Core.Delta;
using ViewEngineServer.Core.Publishing;

namespace ViewEngineServer.IntegrationTests.Engine;

/// <summary>
/// In-memory publisher that records all published events so tests can inspect them.
/// </summary>
internal sealed class CapturingPublisher : IOutboundPublisher
{
    private readonly List<(string ConnectionId, IReadOnlyList<DeltaEvent> Events)> _published = [];

    public IReadOnlyList<(string ConnectionId, IReadOnlyList<DeltaEvent> Events)> Published => _published;

    public ValueTask PublishAsync(string connectionId, IReadOnlyList<DeltaEvent> events,
                                  CancellationToken ct = default)
    {
        _published.Add((connectionId, events));
        return ValueTask.CompletedTask;
    }

    /// <summary>All events for a specific connection, in publish order.</summary>
    public IEnumerable<DeltaEvent> EventsFor(string connectionId) =>
        _published
            .Where(p => p.ConnectionId == connectionId)
            .SelectMany(p => p.Events);
}
