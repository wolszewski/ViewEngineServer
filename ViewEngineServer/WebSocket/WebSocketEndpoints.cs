namespace ViewEngineServer.WebSocket;

public static class WebSocketEndpoints
{
    public static void MapWebSocketEndpoints(this WebApplication app)
    {
        app.MapGet("/ws", async (
            HttpContext context, WebSocketSessionManager sessionManager, CancellationToken ct) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await sessionManager.HandleConnectionAsync(socket, ct);
        });
    }
}
