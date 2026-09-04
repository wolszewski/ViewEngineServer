using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ViewEngineServer.WebApp.WebSocket;

internal sealed class WebSocketConnection(
    int connectionId,
    System.Net.WebSockets.WebSocket socket,
    ILogger<WebSocketOutboundPublisher> logger)
{
    // Unbounded: snapshot delivery writes one message per row (e.g. 10,000+ messages for a large
    // collection) faster than a single-socket drain loop can flush them. A bounded/DropOldest
    // channel here silently discards snapshot rows once full, producing incomplete/inconsistent
    // snapshots on the client. Live delta backpressure is instead handled upstream by
    // LiveDeltaCoalescer/OutboundFlushPolicy, so this channel does not need its own bound.
    private readonly Channel<byte[]> _channel = System.Threading.Channels.Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true
    });

    public System.Net.WebSockets.WebSocket Socket { get; } = socket;
    public Lock Gate { get; } = new();
    public Dictionary<int, SubscriptionState> Subscriptions { get; } = [];
    public Channel<byte[]> Channel => _channel;
    public Task DrainTask { get; private set; } = Task.CompletedTask;
    public int ConnectionId { get; } = connectionId;
    private long _framesWritten;
    private long _framesSent;

    public void StartDrain()
    {
        DrainTask = Task.Run(() => DrainAsync());
    }

    public void Complete()
    {
        _channel.Writer.TryComplete();
    }

    public void TryWrite(byte[] payload)
    {
        if (_channel.Writer.TryWrite(payload))
        {
            Interlocked.Increment(ref _framesWritten);
        }
        else
        {
            logger.LogWarning("Dropping outbound frame for client '{ConnectionId}' (channel closed).", ConnectionId);
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var payload in _channel.Reader.ReadAllAsync())
            {
                if (Socket.State != WebSocketState.Open)
                {
                    logger.LogWarning(
                        "Drain loop for client '{ConnectionId}' stopping: socket state is '{SocketState}' after sending {FramesSent}/{FramesWritten} frames.",
                        ConnectionId, Socket.State, _framesSent, _framesWritten);
                    return;
                }

                try
                {
                    await Socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                    Interlocked.Increment(ref _framesSent);
                }
                catch (WebSocketException ex)
                {
                    logger.LogWarning(
                        ex,
                        "WebSocket send failed for client '{ConnectionId}' after sending {FramesSent}/{FramesWritten} frames; drain loop is stopping and no further messages will be delivered on this connection.",
                        ConnectionId, _framesSent, _framesWritten);
                    return;
                }
            }

            logger.LogInformation(
                "Drain loop for client '{ConnectionId}' completed normally after sending {FramesSent} frames.",
                ConnectionId, _framesSent);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Drain loop for client '{ConnectionId}' terminated unexpectedly after sending {FramesSent}/{FramesWritten} frames.",
                ConnectionId, _framesSent, _framesWritten);
        }
    }
}
