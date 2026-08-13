using LiveViewEngine.Core;
using LiveViewEngine.Core.Output;

namespace LiveViewEngine.Core.IntegrationTests;

internal sealed class CapturingPublisher : IOutboundPublisher
{
    private readonly IOutboundEventFormatter _formatter = new JsonOutboundEventFormatter();
    private readonly List<(string ConnectionId, IReadOnlyList<DeltaEvent> Events)> _published = [];
    public IReadOnlyList<(string ConnectionId, IReadOnlyList<DeltaEvent> Events)> Published => _published;

    public ValueTask PublishAsync(
        IReadOnlyList<string> connectionIds,
        IReadOnlyList<ViewDelta> deltas,
        CancellationToken ct = default)
    {
        var events = _formatter.Format(deltas);
        foreach (var connectionId in connectionIds)
        {
            _published.Add((connectionId, events));
        }
        return ValueTask.CompletedTask;
    }

    public IEnumerable<DeltaEvent> EventsFor(string connectionId) =>
        _published
            .Where(p => p.ConnectionId == connectionId)
            .SelectMany(p => p.Events);
}
