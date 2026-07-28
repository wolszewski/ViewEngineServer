using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ViewEngineServer.Core;
using ViewEngineServer.Core.Views;
using ViewEngineServer.WebSocket.Dto;

namespace ViewEngineServer.WebSocket;

public sealed class WebSocketSessionManager
{
    private readonly IViewEngine _engine;
    private readonly WebSocketOutboundPublisher _publisher;
    private readonly ILogger<WebSocketSessionManager> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public WebSocketSessionManager(
        IViewEngine engine,
        WebSocketOutboundPublisher publisher,
        ILogger<WebSocketSessionManager> logger)
    {
        _engine = engine;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task HandleConnectionAsync(System.Net.WebSockets.WebSocket socket, CancellationToken ct)
    {
        var connectionId = Guid.NewGuid().ToString("N");
        _publisher.Register(connectionId, socket);
        _logger.LogInformation("Client '{ConnectionId}' connected.", connectionId);

        var buffer = new byte[16_384];
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try { result = await socket.ReceiveAsync(buffer, ct); }
                catch (WebSocketException ex)
                {
                    _logger.LogDebug(ex, "Receive error for client '{ConnectionId}'.", connectionId);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                WsInboundMessage? msg;
                try { msg = JsonSerializer.Deserialize<WsInboundMessage>(json, JsonOptions); }
                catch (JsonException ex)
                {
                    _logger.LogDebug(ex, "Invalid JSON from client '{ConnectionId}'.", connectionId);
                    continue;
                }

                if (msg is null)
                {
                    continue;
                }

                var command = MapCommand(connectionId, msg);
                if (command is null)
                {
                    continue;
                }

                var events = await _engine.SubscribeAsync(command, ct);
                if (events.Count > 0)
                {
                    await _publisher.PublishAsync(connectionId, events, ct);
                }
            }
        }
        finally
        {
            _publisher.Unregister(connectionId);
            await _engine.SubscribeAsync(new UnsubscribeCommand { ConnectionId = connectionId },
                CancellationToken.None);

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure,
                        "Disconnected", CancellationToken.None);
                }
                catch (WebSocketException) { /* already closed */ }
            }

            _logger.LogInformation("Client '{ConnectionId}' disconnected.", connectionId);
        }
    }

    private static SubscriptionCommand? MapCommand(string connectionId, WsInboundMessage msg)
    {
        return msg.Type.ToLowerInvariant() switch
        {
            "subscribe" => new SubscribeCommand
            {
                ConnectionId = connectionId,
                StartIndex = msg.StartIndex,
                PageSize = msg.PageSize,
                View = new ViewDefinition
                {
                    CollectionId = msg.CollectionId ?? string.Empty,
                    SortColumn = msg.SortColumn,
                    SortAscending = msg.SortAscending,
                    Filters = msg.Filters?.Select(f => new FilterSpec(
                        f.Field,
                        Enum.TryParse<FilterOperator>(f.Operator, ignoreCase: true, out var op)
                            ? op : FilterOperator.Eq,
                        f.Value)).ToList() ?? []
                }
            },
            "setviewport" => new ChangeViewportCommand
            {
                ConnectionId = connectionId,
                StartIndex = msg.StartIndex,
                PageSize = msg.PageSize
            },
            "unsubscribe" => new UnsubscribeCommand { ConnectionId = connectionId },
            _ => null
        };
    }
}
