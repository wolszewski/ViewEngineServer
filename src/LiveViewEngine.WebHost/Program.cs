using LiveViewEngine.Core;
using ViewEngineServer.WebApp.Tcp;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using ViewEngineServer.WebApp.Http;
using ViewEngineServer.WebApp.WebSocket;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;
var liveViewEngineOptions =
    builder.Configuration.GetSection("LiveViewEngine").Get<LiveViewEngineOptions>() ?? new LiveViewEngineOptions();
var tcpIngestOptions =
    builder.Configuration.GetSection("TcpIngest").Get<TcpIngestOptions>() ?? new TcpIngestOptions();
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

services.AddLiveViewEngineCore(liveViewEngineOptions)
    .AddSorting()
    .AddFiltering()
    .AddProjections();
services.AddLiveViewEnginePublisher<WebSocketOutboundPublisher>();
services.AddSingleton<WebSocketSessionManager>();
services.AddSingleton(tcpIngestOptions);
services.AddSingleton<TcpIngestRequestDispatcher>();
services.AddSingleton<TcpIngestConnectionHandler>();
services.AddHostedService<TcpIngestListenerService>();

var app = builder.Build();
_ = app.Services.GetRequiredService<IViewEngine>();
app.UseWebSockets();
app.MapHttpEndpoints();
app.MapWebSocketEndpoints();

app.Run();
