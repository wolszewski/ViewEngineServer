using LiveViewEngine.Core;
using LiveViewEngine.Core.Output;

namespace LiveViewEngine.Core.IntegrationTests;

internal sealed class CapturingPublisher : IOutboundPublisher
{
    private readonly IOutboundEventFormatter _formatter = new JsonOutboundEventFormatter();
    private readonly List<(int ConnectionId, IReadOnlyList<DeltaEvent> Events)> _published = [];
    private readonly List<(int ConnectionId, IReadOnlyList<ViewDelta> Deltas)> _publishedDeltas = [];
    public IReadOnlyList<(int ConnectionId, IReadOnlyList<DeltaEvent> Events)> Published => _published;
    public IReadOnlyList<(int ConnectionId, IReadOnlyList<ViewDelta> Deltas)> PublishedDeltas => _publishedDeltas;

    public ValueTask PublishAsync(
        IReadOnlyList<SubscriberTarget> targets,
        IReadOnlyList<ViewDelta> deltas,
        CancellationToken ct = default)
    {
        foreach (var target in targets)
        {
            var deltaBatch = deltas.ToArray();
            _publishedDeltas.Add((target.ConnectionId, deltaBatch));

            IReadOnlyList<ViewDelta> formattedDeltas = deltaBatch.Length > 0 && deltaBatch[0] is SnapshotStartDelta
                ? [deltaBatch.ToSnapshotDelta()]
                : deltaBatch;
            _published.Add((target.ConnectionId, _formatter.Format(formattedDeltas, target.SubscriptionId)));
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public IEnumerable<DeltaEvent> EventsFor(int connectionId) =>
        _published
            .Where(p => p.ConnectionId == connectionId)
            .SelectMany(p => p.Events);

    public IEnumerable<IReadOnlyList<ViewDelta>> DeltaBatchesFor(int connectionId) =>
        _publishedDeltas
            .Where(p => p.ConnectionId == connectionId)
            .Select(p => p.Deltas);
}
