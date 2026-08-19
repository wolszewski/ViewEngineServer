# ViewEngineServer

.NET 10 ASP.NET Core server that ingests structured row data and streams live,
sorted/filtered viewport snapshots and incremental delta updates to connected
WebSocket clients — without any sorting or paging work on the client side.

Inspired by Lightstreamer but designed for server-side infinite scroll with
pre-indexed sort buffers, shared view reuse, and a transport-agnostic core.

---

## Architecture

```
[Ingestion adapters]          [Core engine — no HTTP/WS types]     [Output adapters]
  POST /collections    ──►  IViewEngine.IngestAsync()         ──►  WebSocket /ws
  POST /ingest               ├─ CollectionStore                     (IOutboundPublisher)
  TCP ingest port            │   └─ RowCollection
                             ├─ SharedView + SortIndex
                             ├─ ViewportState (per client)
                             └─ Delta engine → DeltaEvent[]
```

**Key design rules:**
- `IViewEngine`, `ICollectionStore`, `IOutboundPublisher` and all core types
  have **zero** dependencies on `HttpContext`, `WebSocket`, TCP streams, or
  any broker SDK.
- Adapters (`Adapters/Http/`, `Adapters/WebSocket/`) are the only code that
  touches transport types.
- The core can be unit-tested and benchmarked in-process without starting a
  server.

---

## Run

```bash
dotnet run
```

---

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET`  | `/`  | Service info and registered collections. |
| `POST` | `/collections` | Register a collection schema. |
| `POST` | `/ingest` | Upsert or delete rows in a collection. |
| `GET`  | `/ws` | WebSocket endpoint for live subscriptions. |
| `TCP`  | `127.0.0.1:6000` | Persistent ingestion socket with schema-aware indexed row updates. |

---

## Quick start

### 1 — Create a collection

```bash
curl -X POST http://localhost:5000/collections \
  -H "Content-Type: application/json" \
  -d '{
    "collectionId": "trades",
    "capacity": 100000,
    "fields": [
      { "name": "tradeId",   "type": "Int32",  "isPrimaryKey": true,  "isSortable": true },
      { "name": "symbol",    "type": "String",  "isSortable": true,  "isFilterable": true },
      { "name": "price",     "type": "Double",  "isSortable": true },
      { "name": "quantity",  "type": "Int32" },
      { "name": "timestamp", "type": "Int64",   "isSortable": true }
    ]
  }'
```

### 2 — Ingest rows

```bash
curl -X POST http://localhost:5000/ingest \
  -H "Content-Type: application/json" \
  -d '{
    "collectionId": "trades",
    "operation": "upsert",
    "fields": {
      "tradeId": 1001, "symbol": "AAPL", "price": 150.25,
      "quantity": 100, "timestamp": 1722000000000
    }
  }'
```

Delete a row:

```bash
curl -X POST http://localhost:5000/ingest \
  -H "Content-Type: application/json" \
  -d '{ "collectionId": "trades", "operation": "delete", "primaryKeyValue": "1001" }'
```

### 3 — Subscribe via WebSocket

Connect to `ws://localhost:5000/ws` and send:

```json
{
  "type": "subscribe",
  "collectionId": "trades",
  "sortColumn": "price",
  "sortAscending": true,
  "startIndex": 0,
  "pageSize": 50
}
```

The server responds with a `snapshot` event and then pushes `rowUpdate`,
`rowInsert`, and `rowRemove` events as data changes.

Change page:

```json
{ "type": "setViewport", "startIndex": 50, "pageSize": 50 }
```

Unsubscribe:

```json
{ "type": "unsubscribe" }
```

---

## WebSocket event types

| `type` | Sent when |
|--------|-----------|
| `snapshot` | Initial subscribe or viewport change. Contains `totalCount`, `startIndex`, `rows[]`. |
| `rowUpdate` | A visible row's field values changed in-place. Contains `rowId`, `position`, `changedFields`. |
| `rowInsert` | A row entered the visible window. Contains `position`, `row`. |
| `rowRemove` | A row left the visible window. Contains `position`. |

---

## TCP ingestion

The server also exposes a persistent TCP ingestion endpoint on `127.0.0.1:6000`
by default. The client can create collections, fetch schema, and upsert/delete
rows by sending newline-framed protocol messages. `UPSERT`/`DELETE` are sent
without waiting for a reply and receive asynchronous `ACK`/`ERR` responses by
default (configurable through `TcpIngest:EnableAsyncAcks`).

See `docs/tcp-ingestion-protocol.md` for the command/response contract.

---

## Filter operators

Supported values for `operator` in filter specs: `eq`, `notEq`, `gt`, `gte`, `lt`, `lte`, `contains`.

Subscribe with filters:

```json
{
  "type": "subscribe",
  "collectionId": "trades",
  "sortColumn": "price",
  "sortAscending": false,
  "filters": [{ "field": "symbol", "operator": "eq", "value": "AAPL" }],
  "startIndex": 0,
  "pageSize": 50
}
```

---

## Project layout

```
ViewEngineServer/
├── Core/                      ← no HTTP/WS dependencies
│   ├── Schema/                  CollectionSchema, FieldDefinition, FieldType
│   ├── Ingestion/               IngestCommand types, IngestResult
│   ├── Storage/                 RowCollection, ICollectionStore
│   ├── Indexing/                SortIndex, FilterSpec, FilterEvaluator
│   ├── Views/                   ViewDefinition, ViewKey, SharedView
│   ├── Subscriptions/           SubscriptionCommand types, ViewportState
│   ├── Delta/                   DeltaEvent types (Snapshot, Update, Insert, Remove)
│   ├── Publishing/              IOutboundPublisher interface
│   └── Engine/                  IViewEngine, ViewEngine (orchestrator)
├── Adapters/
│   ├── Http/                    HttpIngestAdapter, DTOs
│   └── WebSocket/               WebSocketSessionManager, WebSocketOutboundPublisher
└── Program.cs                 ← DI wiring + endpoint mapping only
```
