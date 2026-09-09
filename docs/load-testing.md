# Load testing (NBomber)

`src/loadtests/LiveViewEngine.WebHost.LoadTests` is an NBomber console app that load-tests
`LiveViewEngine.WebHost`'s `/ws` endpoint. It reuses the same protocol contract documented in
[`subscription-design.md`](subscription-design.md), and the existing
`LiveViewEngine.HttpClient` / `LiveViewEngine.TcpClient` libraries for ingestion, so results
reflect the same code paths real clients use.

It uses the official [`NBomber.WebSockets`](https://nbomber.com/docs/protocols/websockets/)
plugin for the WS connections and [`NBomber.Sinks.Timescale`](https://nbomber.com/docs/reporting/realtime/timescale/)
for optional real-time reporting to NBomber Studio.

> NBomber is free for personal use; a license is required for organizational use
> (see [nbomber.com/pricing](https://nbomber.com/pricing)). Every run prints a reminder if
> unlicensed.

## Scenarios

- **`subscribe_latency`** — opens a new WS connection per iteration, sends `subscribe` against a
  pre-seeded collection, and measures time to the full snapshot stream
  (`subscriptionAccepted` → `snapshotStart` → `snapshotRow*` → `eos`).
- **`delta_latency`** — the row in `DeltaCollectionName` carries both `DeltaCreatedAtField` (set
  once on insert, never touched again) and `DeltaUpdatedAtField` (a UTC timestamp rewritten on
  every update tick), matching normal created-vs-updated row semantics. A single background updater
  configured ingest transport. `DeltaSubscriberCount` (default 50) virtual users each open one
  persistent WS subscription (lazily, on that VU's first iteration, reused across iterations) and
  every iteration waits for the next broadcast `rowUpdate`, reporting
  `customLatencyMs = receivedAt - parse(DeltaUpdatedAtField)`. The update rate is independent of
  subscriber count — it's the one shared stream all subscribers observe, matching a "N clients
  watching one periodically-updated collection" workload.

Ingestion is switchable between **HTTP** and **TCP** without touching scenario code
(`--ingest=http|tcp`), since both scenarios go through the shared `IIngestClient` abstraction.

## Running

1. Start the WebHost (bare `dotnet run`, or via `LiveViewEngine.Poc.AppHost`, see below).
2. From `src/loadtests/LiveViewEngine.WebHost.LoadTests`, run:

   ```bash
   # both scenarios, HTTP ingest (defaults)
   dotnet run

   # only the delta scenario, ingesting over TCP instead of HTTP
   dotnet run -- --scenario=delta --ingest=tcp

   # only the subscribe scenario
   dotnet run -- --scenario=subscribe

   # point at a non-default host/ports
   dotnet run -- --ws-url=ws://localhost:5100/ws --http-url=http://localhost:5100 --tcp-port=6000
   ```

   Custom flags (`--ingest`, `--scenario`, `--studio`, `--collection`, `--ws-url`, `--http-url`,
   `--tcp-host`, `--tcp-port`, `--connections`) are consumed by the load test itself; every other
   flag (e.g. `--config`, `--infra-config`) is forwarded to NBomber unchanged, so NBomber's own
   [JSON Config](https://nbomber.com/docs/nbomber/json-config/) overrides (load simulations,
   duration, etc.) work as usual: `dotnet run -- --config=my-config.json`.

   `--connections=N` overrides concurrency for both scenarios at once (`SubscribeCopies` and
   `DeltaSubscriberCount`, default 50 each) - e.g. `dotnet run -- --scenario=delta --connections=200`
   simulates 200 concurrent WS subscribers.

   Default settings live in `appsettings.json` (`LoadTest` section) and can be edited directly
   instead of passing flags every time.

3. Every run writes an HTML/CSV report under `reports/<session-id>/` regardless of any other
   reporting sink.

## NBomber Studio (optional, self-hosted)

There are two ways to run TimescaleDB + NBomber Studio locally with `AUTH__ENABLED=false` —
**no API key/license needed**, since auth is disabled either way:

**Option A: `LiveViewEngine.Poc.AppHost`'s `loadtest-webhost` profile** — starts WebHost (in a
resource-limited container, so its CPU/memory show up in the Aspire dashboard, see below),
TimescaleDB, and NBomber Studio together:

```bash
cd src/examples/LiveViewEngine.Poc.AppHost
dotnet run --launch-profile loadtest-webhost
```

**Option B: standalone `docker compose`** — if you're running WebHost separately (bare `dotnet run`
or another AppHost profile):

```bash
docker compose -f nbomber/docker-compose.yaml up -d
```

Either way, then run the load test with `--studio` to stream results into TimescaleDB as they happen:

```bash
dotnet run -- --studio
```

Open NBomber Studio at <http://localhost:5333> to watch real-time metrics and browse history.
The connection string defaults to match the compose file
(`Host=localhost;Port=5432;Database=nb_studio_db;...`); override via `LoadTest:TimescaleConnectionString`
in `appsettings.json` if you changed the compose file or AppHost ports.

## Observing WebHost CPU/memory during a run

No extra instrumentation is needed:

- `LiveViewEngine.WebHost`'s `Program.cs` already exports OpenTelemetry process + runtime metrics
  via OTLP.
- `LiveViewEngine.Poc.AppHost` already runs the WebHost as a resource-limited container
  (`--cpus=4 --memory=8g`) when started with `--use-webhost-container` (or
  `UseWebHostInContainer=true`).

If you run the WebHost that way, its CPU/memory are visible live in the **Aspire dashboard**
alongside the load test. Otherwise, use `docker stats <container>` for a manual view.

## Out of scope

Lightstreamer load tests are not implemented yet — this project only targets
`LiveViewEngine.WebHost`'s `/ws` endpoint.
