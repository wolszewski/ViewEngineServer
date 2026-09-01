using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Views;
using ViewEngineServer.WebApp.WebSocket.Dto;

namespace ViewEngineServer.WebApp.WebSocket;

public sealed class WebSocketSessionManager
{
    private readonly IViewEngine _engine;
    private readonly ICollectionStore _store;
    private readonly WebSocketOutboundPublisher _publisher;
    private readonly ILogger<WebSocketSessionManager> _logger;
    private readonly UniqueIdProvider _connectionIdProvider = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public WebSocketSessionManager(
        IViewEngine engine,
        ICollectionStore store,
        WebSocketOutboundPublisher publisher,
        ILogger<WebSocketSessionManager> logger)
    {
        _engine = engine;
        _store = store;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task HandleConnectionAsync(System.Net.WebSockets.WebSocket socket, CancellationToken ct)
    {
        var context = new ClientConnectionContext(_connectionIdProvider.Next());
        _publisher.Register(context.ConnectionId, socket);
        _logger.LogInformation("Client '{ConnectionId}' connected.", context.ConnectionId);

        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(context.Buffer, ct);
                }
                catch (WebSocketException ex)
                {
                    _logger.LogDebug(ex, "Receive error for client '{ConnectionId}'.", context.ConnectionId);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                var json = Encoding.UTF8.GetString(context.Buffer, 0, result.Count);
                WsInboundMessage? msg;
                try
                {
                    msg = JsonSerializer.Deserialize<WsInboundMessage>(json, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug(ex, "Invalid JSON from client '{ConnectionId}'.", context.ConnectionId);
                    continue;
                }

                if (msg is null)
                {
                    continue;
                }

                var messageFormat = ResolveMessageFormat(msg.MessageFormat);

                var command = MapCommand(
                    context,
                    msg,
                    out var clientSubscriptionId);
                if (command is null)
                {
                    continue;
                }

                if (command is SubscribeCommand subscribe && clientSubscriptionId > 0)
                {
                    _publisher.ConfigureSubscription(
                        context.ConnectionId,
                        clientSubscriptionId,
                        messageFormat,
                        snapshotActive: subscribe.SendSnapshot);
                }

                bool snapshotBegan = false;
                Action? onBeforeProcess = null;
                if (command is UpdateViewCommand { SnapshotMode: not SnapshotMode.No } && clientSubscriptionId > 0)
                {
                    onBeforeProcess = () =>
                    {
                        snapshotBegan = true;
                        _publisher.BeginViewportSnapshot(context.ConnectionId, command.SubscriptionId);
                    };
                }

                IReadOnlyList<ViewDelta> events;
                try
                {
                    events = await _engine.SubscribeAsync(command, onBeforeProcess, ct);
                }
                catch
                {
                    if (snapshotBegan)
                    {
                        _publisher.CancelSnapshot(context.ConnectionId, command.SubscriptionId);
                    }

                    throw;
                }

                if (snapshotBegan && events.Count == 0)
                {
                    _publisher.CancelSnapshot(context.ConnectionId, command.SubscriptionId);
                }

                if (command is SubscribeCommand subscribeCommand && clientSubscriptionId > 0)
                {
                    var originalEvents = events;
                    var start = TryExtractSnapshotStart(events, out var snapshotEvents);
                    var snapshotFollows = start is not null
                        ? SnapshotFollowsKind.Immediate
                        : ShouldWaitForDeferredSnapshot(subscribeCommand, originalEvents)
                            ? SnapshotFollowsKind.Pending
                            : SnapshotFollowsKind.None;
                    await _publisher.PublishSubscriptionAcceptedAsync(
                        context.ConnectionId,
                        messageFormat,
                        new SubscriptionAcceptedPayload
                        {
                            SubscriptionId = clientSubscriptionId,
                            Fields = ResolvePayloadFieldNames(
                                subscribeCommand.View.CollectionId,
                                originalEvents,
                                subscribeCommand.View.Fields),
                            SnapshotFollows = snapshotFollows,
                            StartIndex = start?.StartIndex ?? subscribeCommand.StartIndex,
                            TotalCount = start?.TotalCount ?? -1
                        },
                        ct);
                    events = snapshotEvents ?? events;
                }

                if (events.Count > 0)
                {
                    await _publisher.PublishAsync(
                        [new SubscriberTarget(context.ConnectionId, command.SubscriptionId)],
                        events,
                        ct);
                }

                if (command is UnsubscribeCommand && clientSubscriptionId > 0)
                {
                    context.ActiveSubscriptionIds.Remove(clientSubscriptionId);
                    _publisher.RemoveSubscription(context.ConnectionId, clientSubscriptionId);
                }
            }
        }
        finally
        {
            _publisher.Unregister(context.ConnectionId);
            await _engine.SubscribeAsync(new UnsubscribeCommand { ConnectionId = context.ConnectionId },
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

            _logger.LogInformation("Client '{ConnectionId}' disconnected.", context.ConnectionId);
        }
    }

    private static SubscriptionCommand? MapCommand(
        ClientConnectionContext context,
        WsInboundMessage msg,
        out int clientSubscriptionId)
    {
        clientSubscriptionId = 0;

        return msg.Type.ToLowerInvariant() switch
        {
            "subscribe" => CreateSubscribeCommand(
                context,
                msg,
                out clientSubscriptionId),
            "updateview" => TryCreateExistingCommand(
                context,
                msg,
                out clientSubscriptionId,
                static (connId, subscriptionId, inbound) => new UpdateViewCommand
                {
                    ConnectionId = connId,
                    SubscriptionId = subscriptionId,
                    StartIndex = inbound.StartIndex,
                    PageSize = inbound.PageSize,
                    SortColumn = inbound.SortColumn,
                    SortAscending = inbound.SortAscending,
                    Filters = inbound.Filters?.Select(f => new FilterSpec(
                        f.Field,
                        Enum.TryParse<FilterOperator>(f.Operator, ignoreCase: true, out var op)
                            ? op : FilterOperator.Eq,
                        f.Value)).ToList(),
                    Fields = inbound.Fields,
                    SnapshotMode = ResolveSnapshotMode(inbound, SnapshotMode.Delta)
                }),
            "setviewport" => TryCreateExistingCommand(
                context,
                msg,
                out clientSubscriptionId,
                static (connId, subscriptionId, inbound) => new UpdateViewCommand
                {
                    ConnectionId = connId,
                    SubscriptionId = subscriptionId,
                    StartIndex = inbound.StartIndex,
                    PageSize = inbound.PageSize,
                    SnapshotMode = ResolveSnapshotMode(inbound, SnapshotMode.Delta)
                }),
            "unsubscribe" => TryCreateExistingCommand(
                context,
                msg,
                out clientSubscriptionId,
                static (connId, subscriptionId, _) => new UnsubscribeCommand
                {
                    ConnectionId = connId,
                    SubscriptionId = subscriptionId
                }),
            _ => null
        };
    }

    private static SubscribeCommand CreateSubscribeCommand(
        ClientConnectionContext context,
        WsInboundMessage msg,
        out int clientSubscriptionId)
    {
        clientSubscriptionId = context.SubscriptionIdProvider.Next();
        while (!context.ActiveSubscriptionIds.Add(clientSubscriptionId))
        {
            clientSubscriptionId = context.SubscriptionIdProvider.Next();
        }

        return BuildSubscribeCommand(context.ConnectionId, clientSubscriptionId, msg);
    }

    private static SubscriptionCommand? TryCreateExistingCommand(
        ClientConnectionContext context,
        WsInboundMessage msg,
        out int clientSubscriptionId,
        Func<int, int, WsInboundMessage, SubscriptionCommand> factory)
    {
        if (msg.SubscriptionId is not { } requestedSubscriptionId ||
            !context.ActiveSubscriptionIds.Contains(requestedSubscriptionId))
        {
            clientSubscriptionId = 0;
            return null;
        }

        clientSubscriptionId = requestedSubscriptionId;
        return factory(context.ConnectionId, clientSubscriptionId, msg);
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
            StartIndex = msg.StartIndex ?? 0,
            PageSize = msg.PageSize,
            SendSnapshot = msg.SendSnapshot ?? true,
            View = new ViewDefinition
            {
                CollectionId = msg.CollectionId ?? string.Empty,
                FilterPresetId = msg.FieldPresetId,
                SortColumn = msg.SortColumn,
                SortAscending = msg.SortAscending ?? true,
                Filters = msg.Filters?.Select(f => new FilterSpec(
                    f.Field,
                    Enum.TryParse<FilterOperator>(f.Operator, ignoreCase: true, out var op)
                        ? op : FilterOperator.Eq,
                    f.Value)).ToList() ?? [],
                Fields = msg.Fields
            }
        };
    }

    private static SnapshotMode ResolveSnapshotMode(WsInboundMessage msg, SnapshotMode defaultMode)
    {
        if (!string.IsNullOrWhiteSpace(msg.SnapshotMode) &&
            Enum.TryParse<SnapshotMode>(msg.SnapshotMode, ignoreCase: true, out var parsedMode))
        {
            return parsedMode;
        }

        if (msg.SendSnapshot.HasValue)
        {
            return msg.SendSnapshot.Value ? SnapshotMode.Full : SnapshotMode.No;
        }

        return defaultMode;
    }

    private static SnapshotStartDelta? TryExtractSnapshotStart(
        IReadOnlyList<ViewDelta> events,
        out IReadOnlyList<ViewDelta>? remainingEvents)
    {
        if (events.Count == 0 || events[0] is not SnapshotStartDelta start)
        {
            remainingEvents = null;
            return null;
        }

        remainingEvents = events.Skip(1).ToArray();
        return start;
    }

    private bool ShouldWaitForDeferredSnapshot(
        SubscribeCommand subscribeCommand,
        IReadOnlyList<ViewDelta> events)
    {
        if (!subscribeCommand.SendSnapshot || events.Count > 0)
        {
            return false;
        }

        return !_store.TryGet(subscribeCommand.View.CollectionId, out _);
    }

    private IReadOnlyList<string> ResolvePayloadFieldNames(
        string collectionId,
        IReadOnlyList<ViewDelta> events,
        IReadOnlyList<string>? requestedFields)
    {
        if (events.FirstOrDefault() is SnapshotStartDelta start)
        {
            var visibleFieldIndexes = start.VisibleFieldIndexes;
            return start.Schema.Fields
                .Where((_, index) => visibleFieldIndexes?.Contains(index) == true && index != CollectionSchema.PrimaryKeyIndex)
                .Select(static field => field.Name)
                .ToArray();
        }

        if (_store.TryGet(collectionId, out var collection) && collection is not null)
        {
            return ResolveCanonicalFieldNames(collection.Schema, requestedFields);
        }

        if (requestedFields is not null)
        {
            return requestedFields;
        }

        return [];
    }

    private static IReadOnlyList<string> ResolveCanonicalFieldNames(
        CollectionSchema schema,
        IReadOnlyList<string>? requestedFields)
    {
        if (requestedFields is null || requestedFields.Count == 0)
        {
            return schema.Fields
                .Where(static field => field.FieldIndex != CollectionSchema.PrimaryKeyIndex)
                .Select(static field => field.Name)
                .ToArray();
        }

        var requested = new HashSet<string>(requestedFields, StringComparer.OrdinalIgnoreCase);
        return schema.Fields
            .Where(field => field.FieldIndex != CollectionSchema.PrimaryKeyIndex && requested.Contains(field.Name))
            .Select(static field => field.Name)
            .ToArray();
    }

    private static OutboundMessageFormat ResolveMessageFormat(string? rawFormat)
    {
        return rawFormat?.Equals("json", StringComparison.OrdinalIgnoreCase) == true
            ? OutboundMessageFormat.Json
            : OutboundMessageFormat.Compact;
    }
    
}