using LiveViewEngine.Core;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using ViewEngineServer.WebApp.Http;
using ViewEngineServer.WebApp.WebSocket;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName))
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter("ViewEngineServer")
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddProcessInstrumentation()
            .AddOtlpExporter();
    });

services.AddLiveViewEngineCore();
services.AddLiveViewEnginePublisher<WebSocketOutboundPublisher>();
services.AddSingleton<WebSocketSessionManager>();

var app = builder.Build();
app.UseWebSockets();
app.MapHttpEndpoints();
app.MapWebSocketEndpoints();

app.Run();
