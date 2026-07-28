using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ViewEngineServer.Core.Delta;
using ViewEngineServer.Core.Publishing;

namespace ViewEngineServer.Adapters.WebSocket;

/// <summary>
/// Implements <see cref="IOutboundPublisher"/> by writing JSON-serialised
/// <see cref="DeltaEvent"/> arrays to a registered WebSocket connection.
///
/// All WebSocket / transport details are encapsulated here; the core engine
/// calls only <see cref="PublishAsync"/> with plain objects.
/// </summary>
public sealed class WebSocketOutboundPublisher : IOutboundPublisher
{
    private readonly ConcurrentDictionary<string, System.Net.WebSockets.WebSocket> _sockets = new();
    private readonly ILogger<WebSocketOutboundPublisher> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public WebSocketOutboundPublisher(ILogger<WebSocketOutboundPublisher> logger)
    {
        _logger = logger;
    }

    public void Register(string connectionId, System.Net.WebSockets.WebSocket socket) =>
        _sockets[connectionId] = socket;

    public void Unregister(string connectionId) =>
        _sockets.TryRemove(connectionId, out _);

    public async ValueTask PublishAsync(string connectionId, IReadOnlyList<DeltaEvent> events,
                                         CancellationToken ct = default)
    {
        if (!_sockets.TryGetValue(connectionId, out var socket)) return;
        if (socket.State != WebSocketState.Open) return;

        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(events, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to serialise events for client '{ConnectionId}'.", connectionId);
            return;
        }

        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket send failed for client '{ConnectionId}'.", connectionId);
            _sockets.TryRemove(connectionId, out _);
        }
    }
}
