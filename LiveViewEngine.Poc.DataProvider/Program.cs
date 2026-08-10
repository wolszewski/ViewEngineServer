using LiveViewEngine.HttpClient;
using LiveViewEngine.Poc.DataProvider.Components;
using LiveViewEngine.Poc.DataProvider.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<TradeGeneratorService>();
builder.Services.AddHttpClient<LiveViewEngineHttpClient>((sp, client) =>
{
    var baseAddress = builder.Configuration["WebHost:BaseUrl"] ?? "http://127.0.0.1:5100";
    client.BaseAddress = new Uri(baseAddress);
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