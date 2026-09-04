using System.Net.WebSockets;

namespace LiveViewEngine.Core.UnitTests;

// WebSocket test double used to exercise WebSocketConnection's DrainAsync loop without a real
// socket: SendBehavior controls what each SendAsync call does, so tests can simulate a healthy
// client, a stalled one (hangs until the drain loop's own per-send timeout cancels it), a dead one
// (throws WebSocketException), or a socket that's already non-Open.
internal sealed class FakeWebSocket : WebSocket
{
    public enum SendBehavior
    {
        Succeed,
        HangUntilCancelled,
        ThrowWebSocketException
    }

    private WebSocketState _state = WebSocketState.Open;
    private int _abortCallCount;

    public SendBehavior Behavior { get; set; } = SendBehavior.Succeed;
    public List<byte[]> SentPayloads { get; } = [];

    public int AbortCallCount => _abortCallCount;
    public bool AbortCalled => _abortCallCount > 0;

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;

    public void SetState(WebSocketState state) => _state = state;

    public override void Abort()
    {
        Interlocked.Increment(ref _abortCallCount);
        _state = WebSocketState.Aborted;
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public override void Dispose()
    {
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        => throw new NotSupportedException("Not exercised by these tests.");

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        switch (Behavior)
        {
            case SendBehavior.ThrowWebSocketException:
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
            case SendBehavior.HangUntilCancelled:
                // Mirrors a client whose TCP receive window is closed: the send never completes on
                // its own, only in response to the caller's own cancellation (WebSocketConnection's
                // per-send stall timeout), at which point this correctly throws
                // OperationCanceledException just like a real hung SendAsync would.
                await Task.Delay(Timeout.Infinite, cancellationToken);
                break;
        }

        SentPayloads.Add(buffer.ToArray());
    }
}
