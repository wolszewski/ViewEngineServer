using LiveViewEngine.Poc.DataProvider.Components;
using LiveViewEngine.Poc.DataProvider.Services;
using LiveViewEngine.TcpClient;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<TradeGeneratorService>();
builder.Services.AddLiveViewEngineTcpIngestionClient(options =>
{
    options.Host = builder.Configuration["TcpIngest:Host"] ?? "127.0.0.1";
    if (int.TryParse(builder.Configuration["TcpIngest:Port"], out var port))
    {
        options.Port = port;
    }
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();