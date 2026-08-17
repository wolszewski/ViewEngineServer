using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using LiveViewEngine.Core;
using LiveViewEngine.Core.Views;
using ViewEngineServer.WebApp.WebSocket.Dto;

namespace ViewEngineServer.WebApp.WebSocket;

public sealed class WebSocketSessionManager
{
    private readonly IViewEngine _engine;
    private readonly WebSocketOutboundPublisher _publisher;
    private readonly ILogger<WebSocketSessionManager> _logger;
    private readonly UniqueIdProvider _connectionIdProvider = new();

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
        var connectionId = _connectionIdProvider.Next();
        _publisher.Register(connectionId, socket);
        _logger.LogInformation("Client '{ConnectionId}' connected.", connectionId);
        var activeSubscriptionIds = new HashSet<int>();
        var subscriptionIdProvider = new UniqueIdProvider();

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

                var command = MapCommand(
                    connectionId,
                    msg,
                    activeSubscriptionIds,
                    subscriptionIdProvider,
                    out var clientSubscriptionId);
                if (command is null)
                {
                    continue;
                }

                if (command is SubscribeCommand && clientSubscriptionId > 0)
                {
                    await _publisher.PublishSubscriptionAcceptedAsync(connectionId, clientSubscriptionId, ct);
                }

                var events = await _engine.SubscribeAsync(command, ct);
                if (events.Count > 0)
                {
                    await _publisher.PublishAsync(
                        [new SubscriberTarget(connectionId, command.SubscriptionId)],
                        events,
                        ct);
                }

                if (command is UnsubscribeCommand && clientSubscriptionId > 0)
                {
                    activeSubscriptionIds.Remove(clientSubscriptionId);
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

    private static SubscriptionCommand? MapCommand(
        int connectionId,
        WsInboundMessage msg,
        ISet<int> activeSubscriptionIds,
        UniqueIdProvider subscriptionIdProvider,
        out int clientSubscriptionId)
    {
        clientSubscriptionId = 0;

        return msg.Type.ToLowerInvariant() switch
        {
            "subscribe" => CreateSubscribeCommand(
                connectionId,
                msg,
                activeSubscriptionIds,
                subscriptionIdProvider,
                out clientSubscriptionId),
            "setviewport" => TryCreateExistingCommand(
                connectionId,
                msg,
                activeSubscriptionIds,
                out clientSubscriptionId,
                static (connId, subscriptionId, inbound) => new ChangeViewportCommand
                {
                    ConnectionId = connId,
                    SubscriptionId = subscriptionId,
                    StartIndex = inbound.StartIndex,
                    PageSize = inbound.PageSize
                }),
            "unsubscribe" => TryCreateExistingCommand(
                connectionId,
                msg,
                activeSubscriptionIds,
                out clientSubscriptionId,
                static (connId, subscriptionId, _) => new UnsubscribeCommand
                {
                    ConnectionId = connId,
                    SubscriptionId = subscriptionId
                }),
            _ => null
        };
    }

    private static SubscribeCommand? CreateSubscribeCommand(
        int connectionId,
        WsInboundMessage msg,
        ISet<int> activeSubscriptionIds,
        UniqueIdProvider subscriptionIdProvider,
        out int clientSubscriptionId)
    {
        if (msg.SubscriptionId is { } existingSubscriptionId)
        {
            if (!activeSubscriptionIds.Contains(existingSubscriptionId))
            {
                clientSubscriptionId = 0;
                return null;
            }

            clientSubscriptionId = existingSubscriptionId;
            return BuildSubscribeCommand(connectionId, clientSubscriptionId, msg);
        }

        clientSubscriptionId = subscriptionIdProvider.Next();
        activeSubscriptionIds.Add(clientSubscriptionId);
        return BuildSubscribeCommand(connectionId, clientSubscriptionId, msg);
    }

    private static SubscriptionCommand? TryCreateExistingCommand(
        int connectionId,
        WsInboundMessage msg,
        ISet<int> activeSubscriptionIds,
        out int clientSubscriptionId,
        Func<int, int, WsInboundMessage, SubscriptionCommand> factory)
    {
        if (msg.SubscriptionId is not { } requestedSubscriptionId ||
            !activeSubscriptionIds.Contains(requestedSubscriptionId))
        {
            clientSubscriptionId = 0;
            return null;
        }

        clientSubscriptionId = requestedSubscriptionId;
        return factory(connectionId, clientSubscriptionId, msg);
    }

    private static SubscribeCommand BuildSubscribeCommand(
        int connectionId,
        int subscriptionId,
        WsInboundMessage msg)
    {
        return new SubscribeCommand
        {
            ConnectionId = connectionId,
            SubscriptionId = subscriptionId,
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
                    f.Value)).ToList() ?? [],
                Fields = msg.Fields
            }
        };
    }

}
