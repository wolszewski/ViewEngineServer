using LiveViewEngine.Core;
using LiveViewEngine.Core.Output;

namespace LiveViewEngine.Core.IntegrationTests;

internal sealed class CapturingPublisher : IOutboundPublisher
{
    private readonly IOutboundEventFormatter _formatter = new JsonOutboundEventFormatter();
    private readonly List<(int ConnectionId, IReadOnlyList<DeltaEvent> Events)> _published = [];
    public IReadOnlyList<(int ConnectionId, IReadOnlyList<DeltaEvent> Events)> Published => _published;

    public ValueTask PublishAsync(
        IReadOnlyList<SubscriberTarget> targets,
        IReadOnlyList<ViewDelta> deltas,
        CancellationToken ct = default)
    {
        foreach (var target in targets)
        {
            _published.Add((target.ConnectionId, _formatter.Format(deltas, target.SubscriptionId)));
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public IEnumerable<DeltaEvent> EventsFor(int connectionId) =>
        _published
            .Where(p => p.ConnectionId == connectionId)
            .SelectMany(p => p.Events);
}
