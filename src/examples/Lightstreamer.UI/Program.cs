using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

var lightstreamerBaseUrl = builder.Configuration["Lightstreamer__BaseUrl"] ?? "http://127.0.0.1:8080";

builder.Services.AddReverseProxy().LoadFromMemory(
[
    new RouteConfig
    {
        RouteId = "lightstreamer",
        ClusterId = "lightstreamer",
        Match = new RouteMatch { Path = "/lightstreamer/{**catch-all}" }
    }
],
[
    new ClusterConfig
    {
        ClusterId = "lightstreamer",
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["primary"] = new() { Address = lightstreamerBaseUrl }
        }
    }
]);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = static context =>
    {
        context.Context.Response.Headers[HeaderNames.CacheControl] = "no-store, no-cache, max-age=0";
        context.Context.Response.Headers[HeaderNames.Pragma] = "no-cache";
        context.Context.Response.Headers[HeaderNames.Expires] = "0";
    }
});
app.MapReverseProxy();

app.Run();
