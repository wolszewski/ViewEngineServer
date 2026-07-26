using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ViewEngineServer.Core.Engine;
using ViewEngineServer.Core.Indexing;
using ViewEngineServer.Core.Subscriptions;
using ViewEngineServer.Core.Views;

namespace ViewEngineServer.Adapters.WebSocket;

// ---------------------------------------------------------------------------
// Inbound message DTO — only used at the WebSocket boundary
// ---------------------------------------------------------------------------

/// <summary>JSON shape of messages sent by front-end clients over WebSocket.</summary>
public sealed class WsInboundMessage
{
    /// <summary>"subscribe" | "setViewport" | "unsubscribe"</summary>
    public string Type { get; set; } = string.Empty;

    // -- subscribe fields --
    public string? CollectionId { get; set; }
    public string? SortColumn { get; set; }
    public bool SortAscending { get; set; } = true;
    public List<WsFilterDto>? Filters { get; set; }
    public int StartIndex { get; set; }
    public int PageSize { get; set; } = 50;
}

public sealed class WsFilterDto
{
    public string Field { get; set; } = string.Empty;

    /// <summary>"eq"|"notEq"|"gt"|"gte"|"lt"|"lte"|"contains"</summary>
    public string Operator { get; set; } = "eq";

    [JsonConverter(typeof(JsonObjectConverter))]
    public object? Value { get; set; }
}

/// <summary>
/// Converts a raw JSON element to its best-fit .NET primitive.
/// Used for filter values that arrive as JSON.
/// </summary>
file sealed class JsonObjectConverter : JsonConverter<object?>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert,
                                  JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True   => true,
            JsonTokenType.False  => false,
            JsonTokenType.Null   => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number =>
                reader.TryGetInt32(out var i) ? i :
                reader.TryGetInt64(out var l) ? l :
                (object?)reader.GetDouble(),
            _ => reader.GetString()
        };

    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, options);
}

// ---------------------------------------------------------------------------
// Session manager — the only place in the WebSocket adapter that touches
// System.Net.WebSockets.WebSocket
// ---------------------------------------------------------------------------

/// <summary>
/// Manages the lifecycle of a single WebSocket connection. Reads inbound
/// messages, maps them to transport-neutral <see cref="SubscriptionCommand"/>s,
/// forwards them to the engine, and dispatches any immediate response events.
/// Ongoing delta events are pushed asynchronously by the engine via
/// <see cref="WebSocketOutboundPublisher"/>.
/// </summary>
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

                if (result.MessageType == WebSocketMessageType.Close) break;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                WsInboundMessage? msg;
                try { msg = JsonSerializer.Deserialize<WsInboundMessage>(json, JsonOptions); }
                catch (JsonException ex)
                {
                    _logger.LogDebug(ex, "Invalid JSON from client '{ConnectionId}'.", connectionId);
                    continue;
                }

                if (msg is null) continue;

                var command = MapCommand(connectionId, msg);
                if (command is null) continue;

                var events = await _engine.SubscribeAsync(command, ct);
                if (events.Count > 0)
                    await _publisher.PublishAsync(connectionId, events, ct);
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
