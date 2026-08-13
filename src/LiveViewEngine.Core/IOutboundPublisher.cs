namespace LiveViewEngine.Core;

public interface IOutboundPublisher
{
    // connectionIds share the same pre-computed deltas so implementations can serialize once and fan out.
    ValueTask PublishAsync(IReadOnlyList<string> connectionIds, IReadOnlyList<ViewDelta> deltas, CancellationToken ct = default);
}
