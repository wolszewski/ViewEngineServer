# ViewEngineServer – System Design

## Overview

ViewEngineServer is an in-memory ASP.NET Core service that keeps named collections of rows and exposes HTTP ingest, TCP ingest, and WebSocket subscription flows. In the current implementation, `IViewEngine` owns the ingest pipeline and the per-view subscription state. `ICollectionStore` keeps each collection alive in memory, and `IOutboundPublisher` pushes JSON/compact delta events to client connections.

This is a deliberately small, server-side data engine: create schemas, ingest row updates, maintain sorted and filtered indexes, and push only the rows currently in a subscriber's viewport.

---

## Current runtime shape

The repo currently implements the following runtime flow:

- `POST /collections` creates a `CollectionSchema`.
- `POST /collections/{collectionName}/ingest` upserts or deletes a row.
- TCP ingest (`127.0.0.1:6000` by default) accepts newline-framed commands (`CREATE`, `GET_SCHEMA`, `UPSERT`, `DELETE`, `PING`).
- `GET /ws` opens a WebSocket session.
- The client sends JSON messages with a `type` of `subscribe`, `updateview`, `setviewport`, or `unsubscribe`.
- `WebSocketSessionManager` maps inbound JSON into `SubscriptionCommand` objects.
- `ViewEngine.SubscribeAsync` returns `ViewDelta` instances for the requesting connection.
- `WebSocketOutboundPublisher` serializes them to JSON and sends them on the socket.
- Subscription lifecycle rules are defined in [subscription-design.md](./subscription-design.md).

For TCP ingest, `UPSERT`/`DELETE` are enqueued into bounded per-collection channels and processed by single consumers to preserve per-collection ordering. Async `ACK`/`ERR` replies for these operations are configurable (`TcpIngest:EnableAsyncAcks`, default `true`).

---

## Components

```
┌──────────────────────────────────────────────────────────────┐
│                     ViewEngineServer                         │
│                                                            │
│  HTTP /collections         HTTP /collections/{name}/ingest │
│  WebSocket /ws                                             │
│         │                              │                   │
│         ▼                              ▼                   │
│  WebSocketSessionManager ──► IViewEngine ──► ICollectionStore │
│         │                                          │            │
│         │                                          ▼            │
│         │                                CollectionStore      │
│         │                                          │            │
│         └──────────────────────────────────────┼────────────┘
│                                                │
│                                                ▼
│                                      RowCollection
│                                        (Dictionary<string,int>
│                                        + SlotList<string?[]>)
│                                                │
│                                                ▼
│                                         SortIndexRegistry
│                                                │
│                                                ▼
│                                          SortIndex
│                                                │
│                                                ▼
│                                         SharedView
│                                             + ViewKey
│                                             + FilterSet
│                                             + ViewportState
│                                                │
│                                                ▼
│                                  MutationPropagator ──► IOutboundPublisher
│                                              │
│                                              ▼
│                               WebSocketOutboundPublisher / Compact+Json encoders
└──────────────────────────────────────────────────────────────┘
```

### `CollectionStore`

`CollectionStore` holds one `RowCollection` per collection name.

- `TryCreate(CollectionSchema schema)` adds a new schema and row store.
- `TryGet(string collectionId, out RowCollection? collection)` returns the live collection.
- `CollectionIds` exposes the current collection names.

### `CollectionSchema`

`CollectionSchema` owns the field layout for a collection.

- The first field is always the primary key (`key` at index 0).
- Additional field names are appended in order from the create-collection request.
- `MapToColumnChanges(...)` converts JSON field dictionaries into `(fieldIndex, value)` pairs used by `RowCollection`.

### `RowCollection`

`RowCollection` is the actual storage backend for one collection.

- It keeps `_rowKeyToIndex: Dictionary<string, int>` for lookup by row key.
- It keeps `_rows: SlotList<string?[]>` as the underlying row storage.
- Each row is an array of `string?`, indexed by the schema field index.
- `AddOrUpdate` writes field values back into the existing row; `Delete` removes a row and returns a `MutationInfo`.

This is more compact than the historical typed-per-column design; the current implementation stores rows as string arrays and reuses a schema for field positions.

### `SortIndex`

`SortIndex` is a per-collection, per-sort-field index built atop `NodeArrayTree<RowComparer>`.

- It tracks the sorted order of row indices by a given field.
- It supports `Take(startIndex, destination)` for page reads.
- `CaptureOldValue` / `OnUpsert` / `OnDelete` coordinate updates during mutation propagation.
- `FilteredDataIndex` and `FilterSet` are layered on top when a view has filters.

### `SharedView`

`SharedView` groups the collection-level sort data with a set of subscriber connection IDs for a specific view definition.

- `ViewKey` encodes `collectionId + sortColumn + sortAscending + filters`.
- `SharedView` reuses a single `SortIndex` for identical view keys.
- `GetPageIndexes` returns the visible indexes for a requested start position and page size.
- `GetTotalCount` returns the filtered count for the current view.

### `ViewportState`

`ViewportState` tracks the currently requested page for a single WebSocket connection.

- `ConnectionId`
- `ViewKey`
- `StartIndex`
- `PageSize`

The viewport is not persisted across reconnects; it is rebuilt on the next subscribe or setviewport message.

### `ViewEngine`

`ViewEngine` orchestrates the ingest pipeline and subscription lifecycle.

- `IngestAsync` handles `CreateCollectionCommand`, `UpsertRowCommand`, and `DeleteRowCommand`.
- `SubscribeAsync` handles `SubscribeCommand`, `UpdateViewCommand`, and `UnsubscribeCommand`.
- `SubscribeCommand` creates or reuses a `SharedView`, creates a `ViewportState`, and sends snapshot deltas.
- `UpdateViewCommand` updates viewport/view settings for an existing subscription and keeps collection binding stable.
- `UnsubscribeCommand` removes viewport state and clears route mapping.

### TCP ingest components

- `TcpIngestListenerService` hosts the TCP socket accept loop.
- `TcpIngestConnectionHandler` reads newline-framed messages, parses protocol requests, and writes response frames.
- `TcpIngestRequestDispatcher` validates requests, handles `CREATE`/`GET_SCHEMA` synchronously, and enqueues `UPSERT`/`DELETE` into per-collection bounded channels.
- Each collection queue has a single reader, so updates for that collection are processed in-order.

### `MutationPropagator`

`MutationPropagator` is the code path that applies row mutations to each active view and emits `ViewDelta` objects.

- It compares the row's prior and new position in each active view.
- It can emit row insert, row remove, row update, or snapshot events.
- It finalizes the outgoing list and hands off to the outbound publisher.

---

## Data flow

### Collection creation

```
POST /collections
 → HttpEndpoints.MapPost("/collections")
 → HttpIngestAdapter.HandleCreateCollectionAsync
 → IViewEngine.IngestAsync(CreateCollectionCommand)
 → ICollectionStore.TryCreate
```

The request body is a JSON object such as:

```json
{
 "collectionName": "orders",
 "fields": ["customer", "amount", "status"]
}
```

### Ingest (upsert / delete)

```
POST /collections/{collectionName}/ingest
 → HttpEndpoints.MapPost("/collections/{collectionName}/ingest")
 → HttpIngestAdapter.HandleIngestAsync
 → IViewEngine.IngestAsync
     → RowCollection.AddOrUpdate / Delete
     → MutationPropagator.PropagateAsync
         for each SharedView in the collection:
             → SortIndex captures old value / updates ordering
             → ViewDelta objects are generated for affected subscribers
             → IOutboundPublisher.PublishAsync
```

The current request body is:

```json
{
 "operation": "upsert",
 "primaryKeyValue": "o42",
 "fields": {
   "customer": "Alice",
   "amount": "99.5",
   "status": "open"
 }
}
```

For delete requests, the server expects:

```json
{
 "operation": "delete",
 "primaryKeyValue": "o42"
}
```

### WebSocket subscribe lifecycle

```
GET /ws
 → WebSocketEndpoints.MapWebSocketEndpoints
 → WebSocketSessionManager.HandleConnectionAsync
 → JSON message decoded to SubscribeCommand / UpdateViewCommand / UnsubscribeCommand
 → IViewEngine.SubscribeAsync
 → SnapshotDelta or viewport delta sent back over the socket
```

The current inbound message types are:

```json
{ "type": "subscribe", "collectionId": "orders", "sortColumn": "amount", "sortAscending": true, "startIndex": 0, "pageSize": 25 }
{ "type": "updateview", "subscriptionId": 1, "startIndex": 50, "pageSize": 25, "sortAscending": false }
{ "type": "setviewport", "subscriptionId": 1, "startIndex": 100, "pageSize": 25 }
{ "type": "unsubscribe", "subscriptionId": 1 }
```

### TCP ingest lifecycle

```
TCP connect
 → CONNECTED|1
 → request line parsed (CREATE / GET_SCHEMA / UPSERT / DELETE / PING)
 → TcpIngestRequestDispatcher
   - CREATE / GET_SCHEMA / PING: immediate response (`SCHEMA` / `ERR` / `PONG`)
   - UPSERT / DELETE: enqueue to per-collection bounded queue
       → queue worker calls IViewEngine.IngestAsync
       → optional async `ACK`/`ERR` frame emitted later on the same connection
```

---

## Current wire format

WebSocket output supports both compact and JSON encodings. Compact is the default; clients can request JSON with `messageFormat: "json"` in the subscribe message. In both cases, the event model is generated from `ViewDelta` types (`SnapshotStartDelta`, `SnapshotDataDelta`, `SnapshotDelta`, `RowUpdateDelta`, `RowInsertDelta`, `RowRemoveDelta`, and snapshot control deltas).

### Snapshot event

```json
{
 "type": "snapshot",
 "viewId": "orders|amount|asc|",
 "totalCount": 1000,
 "startIndex": 0,
 "rows": [
   { "key": "o1", "customer": "Alice", "amount": "99.5", "status": "open" },
   { "key": "o2", "customer": "Bob", "amount": "120", "status": "closed" }
 ]
}
```

### Row insert event

```json
{
 "type": "rowInsert",
 "viewId": "orders|amount|asc|",
 "position": 3,
 "row": { "key": "o42", "customer": "Ivy", "amount": "150", "status": "open" }
}
```

### Row update event

```json
{
 "type": "rowUpdate",
 "viewId": "orders|amount|asc|",
 "rowId": "o42",
 "position": 3,
 "changedFields": { "amount": "135" }
}
```

### Row remove event

```json
{
 "type": "rowRemove",
 "viewId": "orders|amount|asc|",
 "position": 3
}
```

Important: the server currently treats field values as `string?` at the storage boundary. It does not perform typed conversion on ingest, and it does not emit a Lightstreamer-style pipe-delimited payload today.

---

## Planned direction (not yet implemented)

The original design considered a Lightstreamer-like pipe-delimited transport and typed field conversion. That is still a direction for the project, but it is not the current runtime behavior.

The current codebase is intentionally simpler:

- `CollectionSchema` keeps string-based row arrays.
- `SortIndex` and `FilterSet` compare values as strings.
- `WebSocketOutboundPublisher` can emit compact or JSON frames without changing core `ViewDelta` generation.

If the server later adopts a compact wire format, the `ViewDelta` generated by `ViewEngine` can still be transformed independently from the in-memory view logic.

---

## Threading model

Current state is mixed:

- Core state is still shared across HTTP/WebSocket callers using concurrent dictionaries and in-memory mutable structures.
- TCP ingest now introduces per-collection bounded channels with single-consumer workers in `TcpIngestRequestDispatcher`, providing deterministic write ordering for TCP-submitted mutations within each collection.
- HTTP ingest still executes `IViewEngine.IngestAsync` inline on request threads.

So, deterministic per-collection sequencing is guaranteed for TCP queued writes, but the full system is not yet a single serialized actor pipeline across all ingest sources.
