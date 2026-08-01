using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using LiveViewEngine.Core;

namespace ViewEngineServer.WebApp.WebSocket;

public sealed class WebSocketOutboundPublisher(ILogger<WebSocketOutboundPublisher> logger) : IOutboundPublisher
{
    private readonly ConcurrentDictionary<string, System.Net.WebSockets.WebSocket> _sockets = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public void Register(string connectionId, System.Net.WebSockets.WebSocket socket) =>
        _sockets[connectionId] = socket;

    public void Unregister(string connectionId) =>
        _sockets.TryRemove(connectionId, out _);

    public async ValueTask PublishAsync(string connectionId, IReadOnlyList<DeltaEvent> events, CancellationToken ct = default)
    {
        if (!_sockets.TryGetValue(connectionId, out var socket))
        {
            return;
        }

        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(events, JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to serialise events for client '{ConnectionId}'.", connectionId);
            return;
        }

        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (WebSocketException ex)
        {
            logger.LogDebug(ex, "WebSocket send failed for client '{ConnectionId}'.", connectionId);
            _sockets.TryRemove(connectionId, out _);
        }
    }
}
