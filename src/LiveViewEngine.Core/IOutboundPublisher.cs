namespace LiveViewEngine.Core;

public interface IOutboundPublisher
{
    // Targets share the same pre-computed deltas; implementations may still need per-subscription shaping.
    ValueTask PublishAsync(
        IReadOnlyList<SubscriberTarget> targets,
        IReadOnlyList<ViewDelta> deltas,
        CancellationToken ct = default);
}
