using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);
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

// Client-side routes (e.g. /server-sorted, /client-sorted) aren't real files; serve index.html
// for direct navigation/refresh so the SPA router can take over.
app.MapFallbackToFile("index.html");

app.Run();
