using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ViewEngineServer.WebApp.WebSocket;

internal sealed class WebSocketConnection
{
    private readonly Channel<byte[]> _channel;
    private readonly ILogger<WebSocketOutboundPublisher> _logger;
    private readonly TimeSpan _sendStallTimeout;
    private int _completed;
    private int _faulted;
    private long _framesWritten;
    private long _framesSent;

    public WebSocketConnection(
        int connectionId,
        System.Net.WebSockets.WebSocket socket,
        ILogger<WebSocketOutboundPublisher> logger,
        int queueCapacity,
        TimeSpan sendStallTimeout)
    {
        ConnectionId = connectionId;
        Socket = socket;
        _logger = logger;
        _sendStallTimeout = sendStallTimeout;

        // Bounded, not unbounded, as a last-resort memory safety net (see
        // WebSocketOutboundOptions.OutboundQueueCapacity for why this should rarely trip in
        // practice) - the actual slow-client detection is the per-send timeout in DrainAsync below,
        // which reflects whether the client is still making progress rather than how much data is
        // queued. FullMode must be Wait, not DropWrite/DropOldest/DropNewest: TryWrite() is called
        // synchronously (never awaited) here, so Wait mode never blocks it - but critically, Wait is
        // the only mode where a full channel makes TryWrite() return false. DropWrite silently
        // discards the new item while still reporting success (TryWrite returns true), which would
        // make TryWrite's overflow branch below unreachable and let a snapshot reach the client with
        // missing rows/EOS with no fault raised - exactly the silent-data-loss bug this class exists
        // to prevent.
        _channel = System.Threading.Channels.Channel.CreateBounded<byte[]>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });
    }

    public System.Net.WebSockets.WebSocket Socket { get; }
    public Lock Gate { get; } = new();
    public Dictionary<int, SubscriptionState> Subscriptions { get; } = [];
    public Channel<byte[]> Channel => _channel;
    public Task DrainTask { get; private set; } = Task.CompletedTask;
    public int ConnectionId { get; }

    public void StartDrain()
    {
        DrainTask = Task.Run(() => DrainAsync());
    }

    public void Complete()
    {
        Interlocked.Exchange(ref _completed, 1);
        _channel.Writer.TryComplete();
    }

    public void TryWrite(byte[] payload)
    {
        if (_channel.Writer.TryWrite(payload))
        {
            Interlocked.Increment(ref _framesWritten);
            return;
        }

        // The write can fail either because the channel was already completed (Unregister/Complete
        // - an expected, non-faulty teardown race) or because the safety-net capacity was exceeded
        // (a producer bug flooding faster than any client could plausibly drain - see
        // WebSocketOutboundOptions.OutboundQueueCapacity). Only the latter should escalate to an
        // abort; ordinary slow clients are instead caught by DrainAsync's per-send stall timeout.
        if (Volatile.Read(ref _completed) == 0)
        {
            Fault("outbound queue capacity exceeded (possible runaway producer)");
        }
    }

    // Marks the connection as unrecoverably behind and tears it down: stops accepting further
    // frames and aborts the socket so the owning receive loop (WebSocketSessionManager) unblocks
    // and runs its normal Unregister/cleanup path. Idempotent - only the first caller logs/acts,
    // so it's safe to call from both TryWrite (queue full) and DrainAsync (send failed/socket
    // closed) without duplicate log spam or redundant Abort() calls.
    private void Fault(string reason)
    {
        if (Interlocked.CompareExchange(ref _faulted, 1, 0) != 0)
        {
            return;
        }

        _logger.LogWarning(
            "Aborting connection '{ConnectionId}' ({Reason}) after writing {FramesWritten} frames " +
            "({FramesSent} sent) - the client is too slow to keep up or its socket is no longer usable.",
            ConnectionId, reason, _framesWritten, _framesSent);

        // Stop buffering immediately, or a producer racing this Fault() call could keep enqueuing
        // into a channel nobody will ever drain again.
        Interlocked.Exchange(ref _completed, 1);
        _channel.Writer.TryComplete();
        try
        {
            Socket.Abort();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            using var sendCts = new CancellationTokenSource();
            await foreach (var payload in _channel.Reader.ReadAllAsync())
            {
                if (Socket.State != WebSocketState.Open)
                {
                    _logger.LogWarning(
                        "Drain loop for client '{ConnectionId}' stopping: socket state is '{SocketState}' after sending {FramesSent}/{FramesWritten} frames.",
                        ConnectionId, Socket.State, _framesSent, _framesWritten);
                    Fault("socket no longer open");
                    return;
                }

                try
                {
                    // A per-send timeout, not a per-connection/queue-size one: this is what actually
                    // distinguishes a slow-but-progressing client (fine, no matter how large its
                    // snapshot) from one that's genuinely stalled (its TCP receive window is closed,
                    // or the peer is dead) - the latter causes SendAsync to hang rather than fail
                    // immediately, so without this timeout a stalled client would never be detected
                    // at all, just silently stop receiving further frames forever.
                    sendCts.CancelAfter(_sendStallTimeout);
                    await Socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, sendCts.Token);
                    sendCts.CancelAfter(Timeout.InfiniteTimeSpan);
                    Interlocked.Increment(ref _framesSent);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning(
                        "WebSocket send stalled for client '{ConnectionId}' (exceeded {TimeoutSeconds}s) after sending " +
                        "{FramesSent}/{FramesWritten} frames; the client isn't draining fast enough - aborting.",
                        ConnectionId, _sendStallTimeout.TotalSeconds, _framesSent, _framesWritten);
                    Fault("send stalled - client not draining");
                    return;
                }
                catch (WebSocketException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "WebSocket send failed for client '{ConnectionId}' after sending {FramesSent}/{FramesWritten} frames; drain loop is stopping and no further messages will be delivered on this connection.",
                        ConnectionId, _framesSent, _framesWritten);
                    Fault("send failed");
                    return;
                }
            }

            _logger.LogInformation(
                "Drain loop for client '{ConnectionId}' completed normally after sending {FramesSent} frames.",
                ConnectionId, _framesSent);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Drain loop for client '{ConnectionId}' terminated unexpectedly after sending {FramesSent}/{FramesWritten} frames.",
                ConnectionId, _framesSent, _framesWritten);
            Fault("drain loop faulted");
        }
    }
}
