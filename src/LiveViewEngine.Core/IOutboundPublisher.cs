namespace LiveViewEngine.Core;

public interface IOutboundPublisher
{
    ValueTask PublishAsync(string connectionId, IReadOnlyList<ViewDelta> deltas, CancellationToken ct = default);
}
