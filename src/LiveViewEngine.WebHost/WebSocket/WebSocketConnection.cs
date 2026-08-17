using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ViewEngineServer.WebApp.WebSocket;

internal sealed class WebSocketConnection(
    int connectionId,
    System.Net.WebSockets.WebSocket socket,
    ILogger<WebSocketOutboundPublisher> logger)
{
    private readonly Channel<byte[]> _channel = System.Threading.Channels.Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });

    public System.Net.WebSockets.WebSocket Socket { get; } = socket;
    public Lock Gate { get; } = new();
    public Dictionary<int, SubscriptionState> Subscriptions { get; } = [];
    public Channel<byte[]> Channel => _channel;
    public Task DrainTask { get; private set; } = Task.CompletedTask;
    public int ConnectionId { get; } = connectionId;

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
        if (!_channel.Writer.TryWrite(payload))
        {
            logger.LogDebug("Dropping outbound frame for a slow WebSocket client.");
        }
    }

    private async Task DrainAsync()
    {
        await foreach (var payload in _channel.Reader.ReadAllAsync())
        {
            if (Socket.State != WebSocketState.Open)
            {
                break;
            }

            try
            {
                await Socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
            catch (WebSocketException ex)
            {
                logger.LogDebug(ex, "WebSocket send failed for client '{ConnectionId}'.", ConnectionId);
                break;
            }
        }
    }
}
