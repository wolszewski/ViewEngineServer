using Lightstreamer.DataProvider.Blazor.Components;
using Lightstreamer.DataProvider.Blazor.Services;
using LiveViewEngine.Poc.Shared;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<TradeDataProvider>();
builder.Services.AddSingleton<ITradeIngestionClient>(sp => sp.GetRequiredService<TradeDataProvider>());
builder.Services.AddSingleton<TradeGeneratorService>();
builder.Services.AddHostedService<LightstreamerDataProviderServerHost>();

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