using ViewEngineServer.WebApp.Core;
using ViewEngineServer.WebApp.Http;
using ViewEngineServer.WebApp.WebSocket;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ICollectionStore, CollectionStore>();
builder.Services.AddSingleton<WebSocketOutboundPublisher>();
builder.Services.AddSingleton<IOutboundPublisher>(sp =>
    sp.GetRequiredService<WebSocketOutboundPublisher>());
builder.Services.AddSingleton<IViewEngine, ViewEngine>();

builder.Services.AddSingleton<WebSocketSessionManager>();

var app = builder.Build();
app.UseWebSockets();

app.MapHttpEndpoints();
app.MapWebSocketEndpoints();

app.Run();
