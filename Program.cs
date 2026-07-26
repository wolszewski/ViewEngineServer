using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<WebSocketBroadcaster>();

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/", () => Results.Ok(new
{
    service = "ViewEngineServer",
    websocket = "/ws",
    ingest = "/ingest"
}));

app.MapGet("/ws", async (HttpContext context, WebSocketBroadcaster broadcaster, CancellationToken cancellationToken) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var clientId = broadcaster.AddClient(socket);
    await broadcaster.WaitForDisconnectAsync(clientId, socket, cancellationToken);
});

app.MapPost("/ingest", async (HttpRequest request, WebSocketBroadcaster broadcaster, CancellationToken cancellationToken) =>
{
    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    var payload = await reader.ReadToEndAsync(cancellationToken);

    if (string.IsNullOrWhiteSpace(payload))
    {
        return Results.BadRequest(new { error = "Payload is required." });
    }

    var sentTo = await broadcaster.BroadcastAsync(payload, cancellationToken);
    return Results.Accepted(value: new { sentTo });
});

app.Run();

sealed class WebSocketBroadcaster
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();

    public Guid AddClient(WebSocket socket)
    {
        var id = Guid.NewGuid();
        _clients[id] = socket;
        return id;
    }

    public async Task<int> BroadcastAsync(string payload, CancellationToken cancellationToken)
    {
        if (_clients.IsEmpty)
        {
            return 0;
        }

        var message = Encoding.UTF8.GetBytes(payload);
        var deliveries = _clients.Select(client => SendToClientAsync(client.Key, client.Value, message, cancellationToken));
        var results = await Task.WhenAll(deliveries);
        return results.Count(delivered => delivered);
    }

    public async Task WaitForDisconnectAsync(Guid clientId, WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnected", CancellationToken.None);
            }
        }
    }

    private async Task<bool> SendToClientAsync(Guid clientId, WebSocket socket, byte[] payload, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            _clients.TryRemove(clientId, out _);
            return false;
        }

        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
            return true;
        }
        catch (WebSocketException)
        {
            _clients.TryRemove(clientId, out _);
            return false;
        }
    }
}
