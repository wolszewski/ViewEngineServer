# ViewEngineServer – System Design

## Overview

ViewEngineServer is a stateful, in-process real-time data engine. It accepts raw row mutations from producers (via HTTP) and delivers minimal, incremental delta events to consumers (via WebSocket). Consumers subscribe to *views* — named, sorted, filtered windows over named *collections*. Only the rows and fields that change, and only within a subscriber's current viewport, are sent.

The design is inspired by [Lightstreamer](https://lightstreamer.com/): a server-side engine that maintains sorted, filtered virtual tables and pushes compressed cell-level updates to subscribing clients.

---

## Components

```
┌──────────────────────────────────────────────────────┐
│                    ViewEngineServer                   │
│                                                       │
│  HTTP /ingest          WebSocket /ws                  │
│       │                     │                         │
│       ▼                     ▼                         │
│  IViewEngine ◄──────── ViewEngine                     │
│       │                     │                         │
│       │            ┌────────┴────────┐                │
│       │            │                 │                │
│       ▼            ▼                 ▼                │
│  ICollectionStore  SharedView    ViewportState        │
│       │            (per view key)   (per connection)  │
│       ▼                 │                             │
│  RowCollection          │                             │
│  (typed List columns)   ▼                             │
│                    SortIndex                          │
│                    (sorted handle list)               │
│                                                       │
│                    IOutboundPublisher                  │
│                    (WebSocket / test double)           │
└──────────────────────────────────────────────────────┘
```

### `RowCollection`

Stores all rows for one collection. Internally holds one `List<T?>` per schema field, indexed by a stable integer *handle* assigned on first insert. Lists grow on demand — no memory is pre-allocated for the full capacity.

- `GetValue(handle, fieldIndex) → string?` — string representation of the stored value (output path).
- `GetTypedValue(handle, fieldIndex) → object?` — native .NET value for sort/filter comparison (internal path).
- `GetRow(handle) → IReadOnlyDictionary<string, string?>` — all fields as strings.

### `SortIndex`

Maintains a sorted `List<int>` of row handles for a single (column, direction) sort key. Updated incrementally with binary-search insertion/removal on each mutation. Applies filter predicates at read time to serve paginated results.

### `SharedView`

Groups the `SortIndex` and the set of subscriber connection IDs for a given view key (`collectionId + sortColumn + sortAscending + filters`). Multiple connections with identical view parameters share one `SharedView`.

### `ViewportState`

Per-connection scroll position (`startIndex`, `pageSize`) and the row IDs currently in the client's window.

### `ViewEngine`

Orchestrates the full ingest-to-delta pipeline. Serialises per-collection mutations via a `SemaphoreSlim` so every subscriber observes deltas in write order.

---

## Data flows

### Ingest (upsert / delete)

```
HTTP POST /ingest
  → HttpIngestAdapter (JSON → IngestCommand)
  → ViewEngine.IngestAsync
      → RowCollection.Upsert / Delete   (write lock)
      → PropagateMutationAsync               (per-collection semaphore)
          for each SharedView on this collection:
              → SortIndex.OnUpsert / OnDelete
              for each subscriber connection:
                  → BuildDeltas (compare old/new viewport row IDs)
                  → ViewportState.CurrentRowIds updated
                  → IOutboundPublisher.PublishAsync (delta events)
```

### Subscribe

```
WebSocket subscribe message
  → ViewEngine.SubscribeAsync
      → SharedView created or reused (shared SortIndex)
      → ViewportState created
      → SnapshotEvent built from current page handles
      → returned directly to WebSocket handler for immediate send
```

### Viewport change

```
WebSocket changeViewport message
  → ViewEngine.SubscribeAsync (ChangeViewportCommand)
      → ViewportState updated
      → SnapshotEvent built from new page handles
```

---

## Wire format (current and intended)

### Current: JSON delta events

Delta events are JSON-serialised using `System.Text.Json` polymorphic dispatch over WebSocket. Each event carries a `type` discriminator.

**Snapshot** (sent on subscribe or viewport change):
```json
{
  "type": "snapshot",
  "viewId": "orders|amount|asc|",
  "totalCount": 1000,
  "startIndex": 0,
  "rows": [
    { "id": "o1", "customer": "Alice", "amount": "99.5", "status": "open" },
    ...
  ]
}
```

**Row insert** (new row enters the viewport):
```json
{ "type": "rowInsert", "viewId": "...", "position": 3,
  "row": { "id": "o42", "customer": "Bob", "amount": "120", "status": "open" } }
```

**Row update** (field values changed for a row already in viewport):
```json
{ "type": "rowUpdate", "viewId": "...", "rowId": "o42", "position": 3,
  "changedFields": { "amount": "135" } }
```

**Row remove** (row leaves the viewport):
```json
{ "type": "rowRemove", "viewId": "...", "position": 3 }
```

All field values are `string | null`. The server formats each value using the column's native type (e.g. `decimal` → invariant decimal string, `DateTime` → ISO 8601 `"O"` format, `bool` → `"true"` / `"false"`).

### Intended: Lightstreamer-style pipe-delimited encoding

The intended wire optimisation (not yet implemented) is to send rows and updates as pipe-delimited strings, matching the Lightstreamer EXT format:

```
val1|val2||val4
```

- Fields are ordered by the client's subscription field list.
- An empty segment (`||`) means the value is null or unchanged (in update context).
- A snapshot sends all fields for each row.
- An update sends only changed fields; unchanged fields are empty segments.

This format eliminates field-name repetition and JSON overhead. The `GetValue() → string?` API on `RowCollection` is already shaped for this: values are strings at the storage boundary, and pipe-joining them is O(fields) with no further serialisation.

---

## Column type reference

| `FieldType` | .NET type | String format | Notes |
|---|---|---|---|
| `Int32` | `int?` | invariant integer | |
| `Int64` | `long?` | invariant integer | |
| `Decimal` | `decimal?` | invariant decimal | Use for monetary/fractional values |
| `String` | `string?` | as-is | |
| `Boolean` | `bool?` | `"true"` / `"false"` | lowercase |
| `DateTime` | `DateTime?` | ISO 8601 round-trip | `"O"` format spec |
| `DateOnly` | `DateOnly?` | `"yyyy-MM-dd"` | |
| `Byte` | `byte?` | invariant integer | |

---

## Threading model

| Lock | Scope | Purpose |
|---|---|---|
| `ReaderWriterLockSlim` on `RowCollection` | per collection | Protects row storage reads and writes |
| `SemaphoreSlim(1,1)` in `ViewEngine` | per collection | Serialises `PropagateMutationAsync` so deltas are published in write order |
| `Lock` on `SortIndex` | per sort index | Protects sorted handle list during insert/remove |
| `ConcurrentDictionary` | shared views, viewports, mutation locks | Lock-free lookup/registration |
