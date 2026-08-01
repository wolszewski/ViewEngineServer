using LiveViewEngine.Core;
using ViewEngineServer.WebApp.Http;
using ViewEngineServer.WebApp.WebSocket;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;
services.AddLiveViewEngineCore();
services.AddLiveViewEnginePublisher<WebSocketOutboundPublisher>();
services.AddSingleton<WebSocketSessionManager>();

var app = builder.Build();
app.UseWebSockets();
app.MapHttpEndpoints();
app.MapWebSocketEndpoints();

app.Run();
