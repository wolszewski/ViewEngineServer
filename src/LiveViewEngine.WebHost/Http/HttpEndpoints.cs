using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;
using ViewEngineServer.WebApp.Tcp;

namespace ViewEngineServer.WebApp.Http;

public static class HttpEndpoints
{
    public static void MapHttpEndpoints(this WebApplication app)
    {
        app.MapGet("/", (ICollectionStore store, TcpIngestOptions tcpIngest) => Results.Ok(new
        {
            service = "LiveViewEngine.WebHost",
            endpoints = new
            {
                websocket = "/ws",
                collections = "/collections",
                ingest = "/collections/{collectionName}/ingest",
                tcpIngest = new
                {
                    enabled = tcpIngest.Enabled,
                    address = tcpIngest.ListenAddress,
                    port = tcpIngest.Port
                }
            },
            collections = store.CollectionIds
        }));

        app.MapPost("/collections", async (
            HttpRequest request, IViewEngine engine, CancellationToken ct) =>
        {
            var (result, validationError) = await HttpIngestAdapter.HandleCreateCollectionAsync(request, engine, ct);
            if (!result.Success)
            {
                return Results.BadRequest(new { error = result.Error, detail = validationError });
            }

            return Results.Created("/collections", new { message = "Collection created." });
        });

        app.MapPost("/collections/{collectionName}/ingest", async (
            string collectionName,
            HttpRequest request,
            IViewEngine engine,
            CancellationToken ct) =>
        {
            var (result, validationError) =
                await HttpIngestAdapter.HandleIngestAsync(collectionName, request, engine, ct);
            if (!result.Success)
            {
                return Results.BadRequest(new { error = result.Error, detail = validationError });
            }

            return Results.Accepted(value: new { message = "Accepted." });
        });
    }
}
