using ViewEngineServer.WebApp.Core;

namespace ViewEngineServer.IntegrationTests.Core;

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

    public IEnumerable<DeltaEvent> EventsFor(string connectionId) =>
        _published
            .Where(p => p.ConnectionId == connectionId)
            .SelectMany(p => p.Events);
}
