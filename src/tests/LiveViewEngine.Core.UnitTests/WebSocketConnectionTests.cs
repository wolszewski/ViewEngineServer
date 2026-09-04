using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using ViewEngineServer.WebApp.WebSocket;

namespace LiveViewEngine.Core.UnitTests;

public class WebSocketConnectionTests
{
    private static readonly TimeSpan TestWaitTimeout = TimeSpan.FromSeconds(5);

    // Regression test for a real bug: BoundedChannelFullMode.DropWrite silently discards the item
    // being written when the channel is full while still making TryWrite() return true, which made
    // the overflow-detection branch in WebSocketConnection.TryWrite() unreachable - a full queue
    // would drop frames (potentially including EOS) with no fault ever raised. FullMode must be
    // Wait, whose TryWrite() correctly returns false (without blocking, since it's never awaited)
    // once the channel is full, which is what lets TryWrite() escalate to Fault().
    [Fact]
    public void TryWrite_QueueFullBeyondCapacity_FaultsConnectionInsteadOfSilentlyDroppingFrame()
    {
        var socket = new FakeWebSocket();
        var connection = CreateConnection(socket, queueCapacity: 2);

        // Deliberately never call StartDrain(): nothing reads from the channel, so writing beyond
        // capacity exercises the overflow-detection path in isolation from drain-loop timing. The
        // buffered items are never read out, so Channel.Reader.Completion (which only completes once
        // writing is done AND every buffered item has been consumed) is not a usable signal here -
        // Fault() having run is instead observed directly via the socket abort it triggers, and via
        // the writer itself now correctly refusing further writes.
        connection.TryWrite([1]);
        connection.TryWrite([2]);

        Assert.False(socket.AbortCalled);

        // Capacity (2) is now exhausted; this write must overflow and be detected - not silently
        // absorbed the way DropWrite would (which returns true from TryWrite while discarding the
        // item, leaving AbortCalled false and the writer still open).
        connection.TryWrite([3]);

        Assert.True(socket.AbortCalled);
        Assert.False(connection.Channel.Writer.TryWrite([4]));
    }

    [Fact]
    public void TryWrite_WithinCapacity_DoesNotFaultConnection()
    {
        var socket = new FakeWebSocket();
        var connection = CreateConnection(socket, queueCapacity: 2);

        connection.TryWrite([1]);
        connection.TryWrite([2]);

        Assert.False(socket.AbortCalled);

        var written = new List<byte[]>();
        while (connection.Channel.Reader.TryRead(out var item))
        {
            written.Add(item);
        }

        Assert.Equal(2, written.Count);
    }

    // Covers the "expected, non-faulty teardown race" case called out in TryWrite's comment:
    // WebSocketOutboundPublisher.Unregister() calls Complete() unconditionally, and a delta racing
    // that teardown could still call TryWrite() afterwards - this must be silently ignored, not
    // treated as an overflow/fault, since the connection is just shutting down normally.
    [Fact]
    public void TryWrite_AfterNormalComplete_DoesNotFaultConnection()
    {
        var socket = new FakeWebSocket();
        var connection = CreateConnection(socket, queueCapacity: 2);

        connection.Complete();
        connection.TryWrite([1]);

        Assert.False(socket.AbortCalled);
    }

    // Fault() must be safe to reach from multiple overflowing writes (or, in production, racing
    // with a DrainAsync failure) without repeatedly aborting an already-aborted socket.
    [Fact]
    public void TryWrite_OverflowingRepeatedly_OnlyAbortsSocketOnce()
    {
        var socket = new FakeWebSocket();
        var connection = CreateConnection(socket, queueCapacity: 1);

        connection.TryWrite([1]);
        connection.TryWrite([2]);
        connection.TryWrite([3]);
        connection.TryWrite([4]);

        Assert.Equal(1, socket.AbortCallCount);
    }

    [Fact]
    public async Task DrainAsync_SendsBufferedFramesInOrderAndCompletesWithoutFaultOnGracefulShutdown()
    {
        var socket = new FakeWebSocket();
        var connection = CreateConnection(socket, queueCapacity: 10);

        connection.TryWrite([1]);
        connection.TryWrite([2]);
        connection.StartDrain();

        // Complete() mirrors WebSocketOutboundPublisher.Unregister(): no more writes will come, but
        // any already-buffered frames (the two written above) must still be flushed before the
        // drain loop ends.
        connection.Complete();

        await connection.DrainTask.WaitAsync(TestWaitTimeout);

        Assert.False(socket.AbortCalled);
        Assert.Equal(2, socket.SentPayloads.Count);
        Assert.Equal(1, socket.SentPayloads[0][0]);
        Assert.Equal(2, socket.SentPayloads[1][0]);
    }

    [Fact]
    public async Task DrainAsync_SendStallsBeyondTimeout_FaultsConnection()
    {
        var socket = new FakeWebSocket { Behavior = FakeWebSocket.SendBehavior.HangUntilCancelled };
        var connection = CreateConnection(socket, queueCapacity: 10, sendStallTimeout: TimeSpan.FromMilliseconds(50));

        connection.TryWrite([1]);
        connection.StartDrain();

        await connection.DrainTask.WaitAsync(TestWaitTimeout);

        Assert.True(socket.AbortCalled);
        Assert.Empty(socket.SentPayloads);
    }

    [Fact]
    public async Task DrainAsync_SendThrowsWebSocketException_FaultsConnection()
    {
        var socket = new FakeWebSocket { Behavior = FakeWebSocket.SendBehavior.ThrowWebSocketException };
        var connection = CreateConnection(socket, queueCapacity: 10);

        connection.TryWrite([1]);
        connection.StartDrain();

        await connection.DrainTask.WaitAsync(TestWaitTimeout);

        Assert.True(socket.AbortCalled);
        Assert.Empty(socket.SentPayloads);
    }

    [Fact]
    public async Task DrainAsync_SocketAlreadyNotOpen_FaultsConnectionWithoutAttemptingSend()
    {
        var socket = new FakeWebSocket();
        socket.SetState(WebSocketState.Closed);
        var connection = CreateConnection(socket, queueCapacity: 10);

        connection.TryWrite([1]);
        connection.StartDrain();

        await connection.DrainTask.WaitAsync(TestWaitTimeout);

        Assert.True(socket.AbortCalled);
        Assert.Empty(socket.SentPayloads);
    }

    private static WebSocketConnection CreateConnection(
        FakeWebSocket socket, int queueCapacity, TimeSpan? sendStallTimeout = null)
        => new(
            connectionId: 1,
            socket: socket,
            logger: NullLogger<WebSocketOutboundPublisher>.Instance,
            queueCapacity: queueCapacity,
            sendStallTimeout: sendStallTimeout ?? TimeSpan.FromSeconds(30));
}
