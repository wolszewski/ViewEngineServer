# ADR 001 – System Design of ViewEngineServer

## Status

Accepted (amended)

## Context

ViewEngineServer is a real-time data-view engine. Clients subscribe to named *views* over named *collections*, specifying a sort column, optional filters, and a viewport (start index + page size). When rows in a collection are mutated via HTTP ingest or WebSocket commands the server pushes minimal delta events to every affected subscriber rather than re-sending full snapshots.

This ADR records the key design decisions so future contributors understand the *why* behind the architecture.

---

## Decision 1 – Columnar in-memory storage with dynamic-growth per-type lists

**Decision**: Each collection is stored as a `ColumnarCollection` whose data is a set of typed column lists, one per schema field. Each column is a `List<T?>` that grows by one slot each time a new row is inserted. Rows are identified by a stable *handle* (an integer slot index) that is assigned once on first insert and never changes.

**Rationale**:
- Columnar layout is cache-friendly for sort and filter passes, which iterate a single column across many rows.
- Dynamic-growth lists (`List<T?>`) mean only the memory actually required for inserted rows is consumed. A collection of 50 rows uses 50-slot lists regardless of the declared capacity ceiling.
- The handle abstraction decouples the sort index and viewport tracking from the physical slot position, so deletions do not require compaction.

**Trade-offs vs. a pre-allocated array**:
- `List<T?>` has slightly more overhead per access (bounds-checked indexer, backing-array indirection) compared to a raw array. This is negligible compared to the cost of pre-allocating `capacity × field_count` elements regardless of actual use.
- Inserting a new row is O(1) amortised (list append), the same as the previous approach.

**Capacity as a ceiling, not a floor**: `CollectionSchema.Capacity` (default 100 000) is still enforced as a hard row limit — attempting to insert beyond it throws `InvalidOperationException` (enforced in `ColumnarCollection.Upsert` before allocating a new handle). This prevents unbounded growth from runaway producers, but no memory is committed for that ceiling upfront.

**Handle reuse**: Handles are never recycled, so a collection that repeatedly inserts and deletes will eventually exhaust its capacity without ever filling the logical row count. Bulk-load performance may need revisiting at very high throughput since each new slot triggers a list-append.

---

## Decision 2 – Typed column storage with string output API

**Decision**: Column storage uses typed lists (`List<int?>`, `List<long?>`, `List<decimal?>`, `List<string?>`, `List<bool?>`, `List<DateTime?>`, `List<DateOnly?>`, `List<byte?>`) behind a private `IColumn` interface. The public output API returns `string?` — each column formats its stored value to a culture-invariant string representation. A separate internal method returns the typed `object?` for operations that require ordered comparison (sort, filter).

**Supported column types**:

| `FieldType` | .NET backing type | String format |
|---|---|---|
| `Int32` | `int?` | invariant integer |
| `Int64` | `long?` | invariant integer |
| `Decimal` | `decimal?` | invariant decimal |
| `String` | `string?` | as-is |
| `Boolean` | `bool?` | `"true"` / `"false"` |
| `DateTime` | `DateTime?` | ISO 8601 round-trip (`"O"` format) |
| `DateOnly` | `DateOnly?` | `"yyyy-MM-dd"` |
| `Byte` | `byte?` | invariant integer |

**Why string output**: The intended wire format to subscribers is a Lightstreamer-style pipe-delimited string (e.g. `val1|val2||val4` where empty segments represent null/unchanged fields). Converting every field to its string representation at the storage boundary aligns `GetValue()` and `GetRow()` with that wire contract: callers receive `string?` and can pipe-join without further boxing or type dispatch.

**Why keep a typed internal accessor**: Sort and filter operations need ordered comparison (e.g. `100m < 200m`, not `"100" < "200"` which is lexicographic). `ColumnarCollection.GetTypedValue(handle, fieldIndex)` returns `object?` with the stored value as its native .NET type. Only sort-index maintenance and filter evaluation use this path.

**Scope of remaining boxing**:
- `GetTypedValue()` boxes value-type results — unavoidable for the `object?` return type.
- `MutationInfo.PreviousValues / NewValues` are `string?[]` snapshots of changed values; string allocation happens once per mutation for the affected row.
- `SortIndex._handleValues` is `Dictionary<int, object?>` and boxes the sort column value. This is a smaller dataset (one value per live handle, not per field × handle) and is accepted.

---

## Decision 3 – Handle-based sort index with binary insertion

**Decision**: Each `SharedView` owns a `SortIndex` that maintains a sorted `List<int>` of handles. On every upsert or delete the index is updated with a binary-search insertion/removal rather than a full re-sort.

**Rationale**:
- Mutations are expected to be frequent and arrive one at a time. O(log n) binary search for the insertion point followed by O(n) list shift is acceptable for n ≤ 100 000 and avoids re-sorting the entire list on every write.
- A `List<int>` is compact (4 bytes per element) and allows slicing to serve paginated viewport requests without copying the entire result set.

**Trade-offs**:
- High-throughput bulk ingestion (e.g. loading 100 000 rows) will be dominated by O(n) list shifts. An alternative would be a balanced BST or deferred batching, but neither is warranted at current scale.

---

## Decision 4 – Shared views and per-viewport state

**Decision**: A `SharedView` is keyed by `(collectionId, sortColumn, sortAscending, filters)`. Multiple WebSocket connections with identical view parameters share one `SharedView` (and thus one `SortIndex`). Each connection separately tracks its own `ViewportState` (start index, page size, current row IDs).

**Rationale**:
- The sort index is expensive to build and maintain. Sharing it across subscribers with the same logical view eliminates redundant computation.
- Viewport state (scroll position) is per-connection and must remain private.

---

## Decision 5 – Per-collection mutation ordering

**Decision**: A `SemaphoreSlim(1,1)` keyed by `collectionId` serialises calls to `PropagateMutationAsync`. The `ColumnarCollection` itself is protected by a `ReaderWriterLockSlim`.

**Rationale**:
- Sort index updates and delta computation must observe rows in the same order as they were written. Without serialisation two concurrent upserts could push deltas to subscribers out of order.
- `ReaderWriterLockSlim` on the collection allows concurrent reads (e.g. building snapshots for multiple subscribers simultaneously) while still protecting writes.

---

## Decision 6 – Stateless HTTP ingest + WebSocket subscriptions

**Decision**: Row mutations arrive over a plain HTTP REST API (`POST /ingest`). View subscriptions and viewport changes arrive over WebSocket. Delta events are pushed back over the same WebSocket.

**Rationale**:
- HTTP is a natural fit for fire-and-forget ingest: the producer gets a synchronous acknowledgement, connection pooling is handled by the HTTP stack, and the endpoint is easy to call from any language.
- WebSocket is required for push-based delta streaming; maintaining a long-lived connection per subscriber is the only practical option for real-time updates.

---

## Consequences

- Adding a new `FieldType` requires a new typed column class and a case in the constructor switch expression in `ColumnarCollection`.
- The `Capacity` ceiling is intentional and must be documented to integrators; the limit is enforced in `ColumnarCollection.Upsert` before allocating a new handle.
- Numeric and temporal filtering (e.g. `amount > 100`) works correctly because `FilterEvaluator` receives typed values via `GetTypedValue`, not the string representation.
- The sort index stores typed sort values obtained via `GetTypedValue` at mutation time, not from `MutationInfo.NewValues` (which is string).
