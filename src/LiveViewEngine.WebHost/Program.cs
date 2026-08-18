using LiveViewEngine.Core;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using ViewEngineServer.WebApp.Http;
using ViewEngineServer.WebApp.WebSocket;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;
var liveViewEngineOptions =
    builder.Configuration.GetSection("LiveViewEngine").Get<LiveViewEngineOptions>() ?? new LiveViewEngineOptions();
var otlpEndpoint =
    builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
    ?? builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"];

services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName))
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter("ViewEngineServer")
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddProcessInstrumentation()
            .AddOtlpExporter(options =>
            {
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    options.Endpoint = new Uri(otlpEndpoint);
                }
            });
    });

services.AddLiveViewEngineCore(liveViewEngineOptions);
services.AddLiveViewEnginePublisher<WebSocketOutboundPublisher>();
services.AddSingleton<WebSocketSessionManager>();

var app = builder.Build();
_ = app.Services.GetRequiredService<IViewEngine>();
app.UseWebSockets();
app.MapHttpEndpoints();
app.MapWebSocketEndpoints();

app.Run();
