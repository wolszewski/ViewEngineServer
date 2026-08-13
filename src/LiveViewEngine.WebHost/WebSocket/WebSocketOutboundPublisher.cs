using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using LiveViewEngine.Core;
using LiveViewEngine.Core.Output;

namespace ViewEngineServer.WebApp.WebSocket;

public sealed class WebSocketOutboundPublisher(
    IOutboundEventFormatter formatter,
    ILogger<WebSocketOutboundPublisher> logger) : IOutboundPublisher
{
    private readonly ConcurrentDictionary<string, ConnectionState> _connections = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Called when a WebSocket connection is accepted; starts a background drain consumer.
    public void Register(string connectionId, System.Net.WebSockets.WebSocket socket)
    {
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        var state = new ConnectionState(socket, channel);
        _connections[connectionId] = state;
        state.DrainTask = Task.Run(() => DrainAsync(connectionId, socket, channel.Reader));
    }

    public void Unregister(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var state))
        {
            state.Channel.Writer.TryComplete();
        }
    }

    // Serialize the deltas once, then enqueue the bytes for each connection.
    // Returns immediately — slow clients drop stale frames, never block ingestion.
    public ValueTask PublishAsync(
        IReadOnlyList<string> connectionIds,
        IReadOnlyList<ViewDelta> deltas,
        CancellationToken ct = default)
    {
        byte[] payload;
        try
        {
            var events = formatter.Format(deltas);
            payload = JsonSerializer.SerializeToUtf8Bytes(events, JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to serialise events for {Count} client(s).", connectionIds.Count);
            return ValueTask.CompletedTask;
        }

        foreach (var connectionId in connectionIds)
        {
            if (_connections.TryGetValue(connectionId, out var state))
            {
                state.Channel.Writer.TryWrite(payload);
            }
        }

        return ValueTask.CompletedTask;
    }

    private async Task DrainAsync(
        string connectionId,
        System.Net.WebSockets.WebSocket socket,
        ChannelReader<byte[]> reader)
    {
        await foreach (var payload in reader.ReadAllAsync())
        {
            if (socket.State != WebSocketState.Open)
            {
                break;
            }

            try
            {
                await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
            catch (WebSocketException ex)
            {
                logger.LogDebug(ex, "WebSocket send failed for client '{ConnectionId}'.", connectionId);
                break;
            }
        }
    }

    private sealed class ConnectionState(
        System.Net.WebSockets.WebSocket socket,
        Channel<byte[]> channel)
    {
        public readonly System.Net.WebSockets.WebSocket Socket = socket;
        public readonly Channel<byte[]> Channel = channel;
        public Task DrainTask = Task.CompletedTask;
    }
}
