namespace ViewEngineServer.WebApp.Core;

public interface IOutboundPublisher
{
    ValueTask PublishAsync(string connectionId, IReadOnlyList<DeltaEvent> events,
                           CancellationToken ct = default);
}
