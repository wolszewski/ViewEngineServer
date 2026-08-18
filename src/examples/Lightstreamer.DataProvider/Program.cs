using Lightstreamer.DataProvider.Components;
using Lightstreamer.DataProvider.Services;
using LiveViewEngine.Poc.Shared;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<TradeCommandProvider>();
builder.Services.AddSingleton<TradeMergeDataProvider>();
builder.Services.AddSingleton<ITradeIngestionClient>(sp => sp.GetRequiredService<TradeMergeDataProvider>());
builder.Services.AddSingleton<TradeGeneratorService>();
builder.Services.AddSingleton<TradeGenerationSettingsStore>();
builder.Services.AddHostedService<TradeGenerationLifecycleHostedService>();

builder.Services.AddHostedService(sp =>
{
    var host = "127.0.0.1";
    var port = 6661;
    var adapterName = "trades-merge-adapter";
    return new LightstreamerDataProviderServerHost<TradeMergeDataProvider>(
        sp.GetRequiredService<TradeMergeDataProvider>(),
        host, port, adapterName,
        sp.GetRequiredService<ILogger<LightstreamerDataProviderServerHost<TradeMergeDataProvider>>>());
});

builder.Services.AddHostedService(sp =>
{
    var host =  "127.0.0.1";
    var port = 6662;
    var adapterName = "trades-command-adapter";
    return new LightstreamerDataProviderServerHost<TradeCommandProvider>(
        sp.GetRequiredService<TradeCommandProvider>(),
        host, port, adapterName,
        sp.GetRequiredService<ILogger<LightstreamerDataProviderServerHost<TradeCommandProvider>>>());
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

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Run();
