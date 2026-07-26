# ViewEngineServer

.NET 10 ASP.NET Core server that accepts updates from upstream services and immediately pushes them to connected frontend clients over WebSockets.

## Run

```bash
dotnet run
```

## Endpoints

- `GET /ws` - WebSocket endpoint for frontend clients.
- `POST /ingest` - Send update payload (text/JSON body) to broadcast instantly to all connected WebSocket clients.
- `GET /` - Basic service info.