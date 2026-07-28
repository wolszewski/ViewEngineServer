# ADR 001 – System Design of ViewEngineServer

## Status

Accepted

## Context

ViewEngineServer is a real-time data-view engine. Clients subscribe to named *views* over named *collections*, specifying a sort column, optional filters, and a viewport (start index + page size). When rows in a collection are mutated via HTTP ingest or WebSocket commands the server pushes minimal delta events to every affected subscriber rather than re-sending full snapshots.

The first working implementation was built in the PR that merged the basic system. This ADR records the key decisions taken there so future contributors can understand the *why* behind the design.

---

## Decision 1 – Columnar in-memory storage with pre-allocated arrays

**Decision**: Each collection is stored as a `ColumnarCollection` whose data is a set of typed column arrays, one per schema field. Each column is an array of length `capacity` (default 100 000). Rows are identified by a stable *handle* (an integer slot index) that is assigned once on first insert and never changes.

**Rationale**:
- Columnar layout is cache-friendly for sort and filter passes, which iterate a single column across many rows.
- Pre-allocation avoids incremental resizing and gives O(1) random access by handle.
- The handle abstraction decouples the sort index and viewport tracking from the physical slot position, so deletions do not require compaction.

**Trade-offs**:
- The full `capacity` memory is committed at creation time even if the collection is sparse.
- Handles are never recycled, so a collection that repeatedly inserts and deletes will eventually exhaust its capacity without ever filling the logical row count.

---

## Decision 2 – Typed column arrays (no boxing for stored values)

**Decision**: Column storage uses typed arrays (`int?[]`, `long?[]`, `double?[]`, `string?[]`, `bool?[]`) behind an internal `IColumn` interface, rather than a single `object?[][]`.

**Rationale**:
- Storing value types (`Int32`, `Int64`, `Double`, `Boolean`) in an `object?[]` array boxes each value onto the heap. At 100 000 rows with several numeric columns this creates hundreds of thousands of small heap objects, increasing GC pause frequency and consuming roughly 6× more memory than a raw value-type array.
- With typed arrays the values live contiguously in array memory as structs. Boxing is deferred to the moment a value crosses the `object?` API boundary (snapshot/delta serialisation), where it is unavoidable anyway.
- The `IColumn` abstraction keeps the `ColumnarCollection` code simple: write, read, and clear operations delegate to the column implementation; the rest of the class is unchanged.

**Scope of remaining boxing**:
- `GetValue(int handle, int fieldIndex) → object?` boxes on read. This is intentional: callers need `object?` for JSON serialisation and for the filter/sort comparers.
- `MutationInfo.PreviousValues / NewValues` are `object?[]` snapshots of changed values; boxing happens once per mutation for the affected row.
- `SortIndex._handleValues` is `Dictionary<int, object?>` and boxes the sort column value. This is a smaller dataset (one value per live handle, not per field × handle) and is left as-is.

---

## Decision 3 – Handle-based sort index with binary insertion

**Decision**: Each `SharedView` owns a `SortIndex` that maintains a sorted `List<int>` of handles. On every upsert or delete the index is updated with a binary-search insertion/removal rather than a full re-sort.

**Rationale**:
- Mutations are expected to be frequent and arrive one at a time. O(log n) binary search for the insertion point followed by O(n) list shift is acceptable for n ≤ 100 000 and avoids re-sorting the entire list on every write.
- A `List<int>` is compact (4 bytes per element) and allows slicing to serve paginated viewport requests without copying the entire result set.

**Trade-offs**:
- High-throughput bulk ingestion (e.g. loading 100 000 rows) will be dominated by O(n) list shifts. An alternative would be a balanced BST (e.g. `SortedList`) or deferred batching, but neither is warranted at current scale.

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
- The `capacity` limit is intentional and must be documented to integrators; attempting to insert beyond capacity throws `InvalidOperationException` (enforced in `ColumnarCollection.Upsert` before allocating a new handle).
- Bulk-load performance may need revisiting if the expected row count approaches 100 000 with high write throughput.
