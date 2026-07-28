using ViewEngineServer.Adapters.Http;
using ViewEngineServer.Adapters.WebSocket;
using ViewEngineServer.Core.Engine;
using ViewEngineServer.Core.Publishing;
using ViewEngineServer.Core.Storage;

var builder = WebApplication.CreateBuilder(args);

// -------------------------------------------------------------------------
// Core services — no HTTP / WebSocket types inside these registrations
// -------------------------------------------------------------------------
builder.Services.AddSingleton<ICollectionStore, CollectionStore>();
builder.Services.AddSingleton<WebSocketOutboundPublisher>();
builder.Services.AddSingleton<IOutboundPublisher>(sp =>
    sp.GetRequiredService<WebSocketOutboundPublisher>());
builder.Services.AddSingleton<IViewEngine, ViewEngine>();

// -------------------------------------------------------------------------
// Adapter services
// -------------------------------------------------------------------------
builder.Services.AddSingleton<WebSocketSessionManager>();

var app = builder.Build();
app.UseWebSockets();

// -------------------------------------------------------------------------
// Endpoints — thin; all logic delegated to adapters / engine
// -------------------------------------------------------------------------

app.MapGet("/", (ICollectionStore store) => Results.Ok(new
{
    service = "ViewEngineServer",
    endpoints = new { websocket = "/ws", collections = "/collections", ingest = "/ingest" },
    collections = store.CollectionIds
}));

// Register a new collection schema
app.MapPost("/collections", async (
    HttpRequest request, IViewEngine engine, CancellationToken ct) =>
{
    var (result, validationError) = await HttpIngestAdapter.HandleCreateCollectionAsync(request, engine, ct);
    if (!result.Success)
        return Results.BadRequest(new { error = result.Error, detail = validationError });
    return Results.Created("/collections", new { message = "Collection created." });
});

// Upsert or delete rows
app.MapPost("/ingest", async (
    HttpRequest request, IViewEngine engine, CancellationToken ct) =>
{
    var (result, validationError) = await HttpIngestAdapter.HandleIngestAsync(request, engine, ct);
    if (!result.Success)
        return Results.BadRequest(new { error = result.Error, detail = validationError });
    return Results.Accepted(value: new { message = "Accepted." });
});

// WebSocket endpoint for live-updating client subscriptions
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

app.Run();
