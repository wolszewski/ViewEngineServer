# Architecture review — 2026-09-12

| | |
|---|---|
| **Scope** | Whole solution, focusing on snapshot delivery and slow-client handling on the WebSocket path |
| **Baseline** | `main` @ `94319f9` (Merge PR #32) |
| **Method** | Code reading of Core, WebHost, TcpProtocol, the clients and the PoC UI. Suspected defects were reproduced against a running WebHost or with a scratch program that calls the encoders and store directly. |
| **Follow-up docs** | [system-design.md](../system-design.md) (incl. *Snapshots and slow clients*, *Known issues*), [websocket-protocol.md](../websocket-protocol.md) |

Conventions for this folder are in [README.md](README.md). Finding IDs (`AR-NN`) are stable. Reference a
finding as `2026-09-12 AR-05` in intents, commits and PRs. Update only the **Status** column below when
work lands.

**Verification levels:**

- **Reproduced:** observed on a running server or in a scratch program.
- **Code-read:** follows directly from the code, not measured.

**Severity:**

- **High:** data corruption, a denial of service that clients can trigger, or unbounded memory.
- **Medium:** a wrong or degraded result, or a noticeable waste of resources.
- **Low:** hygiene and clarity.

## Summary

The core design is sound. Each collection has one worker, so there is no locking and snapshots are
consistent with the updates that follow them. Views and delta computation are shared across subscribers.
The engine has no transport dependencies, and outbound sends never block. Most problems sit at the
WebSocket edge:

- compact-encoder defects that corrupt what clients decode;
- client input that tears down connections;
- no memory bound for clients that are slow but still receiving.

## Index

| ID | Title | Area | Severity | Verified | Status |
|---|---|---|---|---|---|
| [AR-01](#ar-01) | Compact `A` frame has an extra token when no snapshot follows | Protocol | High | Reproduced | Open |
| [AR-02](#ar-02) | Compact encoder corrupts non-BMP characters (emoji) | Protocol | Medium | Reproduced | Open |
| [AR-03](#ar-03) | Compact key-only projection (`fields: []`) drops rows | Protocol | Medium | Reproduced | Open |
| [AR-04](#ar-04) | Duplicate collection create leaks a started runtime | Core | Medium | Code-read | Open |
| [AR-05](#ar-05) | Invalid client input closes the WebSocket with no error | WebSocket | High | Reproduced | Open |
| [AR-06](#ar-06) | Unknown `fieldPresetId` is reported as accepted but no subscription exists | Core | Medium | Code-read | Open |
| [AR-07](#ar-07) | Live deltas can precede `subscriptionAccepted` when `sendSnapshot:false` | WebSocket | Low | Code-read | Open |
| [AR-08](#ar-08) | Schemas over 128 fields are accepted but unsupported | Core | Low | Code-read | Open |
| [AR-09](#ar-09) | No memory bound for slow-but-progressing clients | Slow clients | High | Code-read | Open |
| [AR-10](#ar-10) | Live-delta coalescer is effectively dead and would reorder positions if enabled | Slow clients | Medium | Code-read | Open |
| [AR-11](#ar-11) | `FlushAsync` visits every connection after every mutation | Performance | Medium | Code-read | Open |
| [AR-12](#ar-12) | Snapshot size is unbounded, built on the worker, and faults above queue capacity | Snapshots | High | Code-read | Open |
| [AR-13](#ar-13) | Deltas are encoded once per subscriber, on the collection worker | Performance | Medium | Code-read | Open |
| [AR-14](#ar-14) | One WebSocket message per row/delta | Performance | Low | Code-read | Open |
| [AR-15](#ar-15) | Redundant `rowUpdate` after `rowInsert`/`rowReplace` of the same row | Protocol | Low | Reproduced | Open |
| [AR-16](#ar-16) | No metrics for outbound queues or slow-client disconnects | Observability | Medium | Code-read | Open |
| [AR-17](#ar-17) | Two collection registries, and host code can reach non-thread-safe storage | Architecture | Low | Code-read | Open |
| [AR-18](#ar-18) | TCP ingest: double queueing, `ACK` = queued, comparer mismatch | Ingest | Low | Code-read | Open |
| [AR-19](#ar-19) | HTTP ingest has no backpressure (unbounded worker queue) | Ingest | Low | Code-read | Open |
| [AR-20](#ar-20) | Snapshot enumeration does an `O(log n)` lookup per row | Performance | Low | Code-read | Open |
| [AR-21](#ar-21) | Allocating/noisy logging on the snapshot path | Performance | Low | Code-read | Open |
| [AR-22](#ar-22) | OpenTelemetry packages have known vulnerabilities | Dependencies | Medium | Reproduced | Open |
| [AR-23](#ar-23) | Dead code | Maintainability | Low | Code-read | Open |
| [AR-24](#ar-24) | Naming inconsistencies | Maintainability | Low | Code-read | Open |
| [AR-25](#ar-25) | Compact `P` partial flag is ambiguous | Protocol | Low | Code-read | Open |

## Suggested order

1. **Correctness quick wins:** AR-01, AR-02, AR-03, AR-04, AR-05, AR-06, AR-08, AR-22. Each is a small,
   local fix with a regression test.
2. **Slow clients:** AR-16 first (so the effect can be measured), then simplify the flush path (AR-10 +
   AR-11), then bound memory (AR-09) and snapshots (AR-12). This group needs an intent document (see
   [../changes/README.md](../changes/README.md)).
3. **Throughput:** AR-13, AR-15, AR-20, AR-21, AR-18, then AR-14 together with AR-25 as a protocol v2.
4. **Hygiene:** AR-07, AR-17, AR-19, AR-23, AR-24.

---

## Correctness

### AR-01

**Compact `A` frame has an extra token when no snapshot follows** · High · Reproduced

- **Where:** `src/LiveViewEngine.WebHost/WebSocket/CompactOutboundProtocolEncoder.cs:38`
- **Problem:** When `SnapshotFollows` is false, the flag position gets a separator instead of an empty token.
- **Evidence:** `SnapshotFollows=false` produces `A|7|||0|42|price|qty` instead of `A|7||0|42|price|qty`.
- **Impact:** Compact clients subscribing with `sendSnapshot:false` read `startIndex=""`, `totalCount=0` and
  fields `["42","price","qty"]`. Every later `I`/`U`/`R` frame then maps values onto the wrong columns.
- **Fix:** Write `OneByte` only when the flag is set, and nothing otherwise.
- **Done when:** An encoder unit test covers both flag values (token count and positions). The
  "Known compact-format defects" note in `websocket-protocol.md` is removed.

### AR-02

**Compact encoder corrupts non-BMP characters** · Medium · Reproduced

- **Where:** `CompactOutboundProtocolEncoder.cs:384` (`WriteEscaped`)
- **Problem:** Each UTF-16 code unit is UTF-8-encoded on its own. A lone surrogate half becomes `U+FFFD`.
- **Evidence:** `"rocket 🚀"` is encoded as `rocket ��`. The JSON encoder is correct.
- **Fix:** Encode runs of unescaped characters with a single `Encoding.UTF8.GetBytes(span)` call, inserting
  escape bytes between runs, or iterate by `Rune`. This is also faster than per-character encoding.
- **Done when:** A test round-trips emoji, CJK, escapes (`| \ ^ ~`), `null` and the empty string. An encoder
  benchmark shows no regression.

### AR-03

**Compact key-only projection drops rows** · Medium · Reproduced

- **Where:** `src/LiveViewEngine.WebHost/WebSocket/OutboundProtocolEncodingHelpers.cs:28`
- **Problem:** `GetPayloadFieldIndexes` falls back to all fields when the projection has no non-key
  fields. `FindSelectedFieldPosition` then throws `Field index '1' was not selected`. `PublishDelta` logs
  the error and drops the batch (snapshot rows, and live `I`/`R` frames).
- **Fix:** Fall back only when `visibleFieldIndexes is null`. A key-only projection has an empty payload.
- **Done when:** Compact tests for `fields: []` cover a snapshot, an insert and a replace. The PoC client
  handles an empty field list.

### AR-04

**Duplicate collection create leaks a started runtime** · Medium · Code-read

- **Where:** `src/LiveViewEngine.Core/Data/CollectionStore.cs:23-24`
- **Problem:** `new CollectionRuntime(...)` starts its worker task in the constructor. If `TryAdd` then
  fails, the runtime is never disposed. Its worker keeps waiting on its channel, which keeps the runtime and
  its `RowCollection` alive forever.
- **Impact:** Producers that "create if missing" on every start leak one runtime per start. The PoC
  `TradeGeneratorService` does this.
- **Fix:** Check-then-create under a lock, `GetOrAdd` with `Lazy<CollectionRuntime>`, or dispose the
  runtime when `TryAdd` fails.
- **Done when:** A test creates the same collection repeatedly and confirms no extra runtime stays alive
  (for example, the discarded runtime's worker completes).

### AR-05

**Invalid client input closes the WebSocket with no error** · High · Reproduced

- **Where:** `src/LiveViewEngine.WebHost/WebSocket/WebSocketSessionManager.cs:126` (rethrow) and `:250`
  (`msg.Type.ToLowerInvariant()`)
- **Problem:** Exceptions from the engine escape the receive loop. Examples: `ArgumentException` for an
  unknown name in `fields`, `InvalidOperationException` for `updateview` on a subscription the engine no
  longer knows, and a `NullReferenceException` for `"type": null`.
- **Evidence:** Both inputs close the socket with `NormalClosure` and no frame. The server logs an unhandled
  exception in the endpoint.
- **Related:** `ReceiveAsync` ignores `EndOfMessage`, so a message over 16 KiB is parsed as several invalid
  fragments.
- **Fix:**
  - Treat invalid requests as data, the same way the capability rejections already are. Validate `fields`
    in the runtime and return a `SubscriptionRejectedDelta` with reason `invalid_request`.
  - In the session loop, catch per message and reply with `subscriptionRejected` or `updateRejected`.
  - Handle a null `Type`.
  - Assemble fragmented messages up to a configured maximum size, and close with `MessageTooBig` beyond it.
- **Done when:** Session-manager tests cover each case, the connection stays open, and the right rejection
  frame is sent.

### AR-06

**Unknown `fieldPresetId` is reported as accepted but no subscription exists** · Medium · Code-read

- **Where:** `src/LiveViewEngine.Core/Runtime/CollectionRuntime.cs:158-161`
- **Problem:** An unknown preset returns `[]`. The session treats that as success and sends
  `subscriptionAccepted` (no snapshot, `totalCount -1`), but no viewport is registered. A later `updateview`
  hits AR-05.
- **Fix:** Return a `SubscriptionRejectedDelta` with reason `filter_preset_not_found`, and document the code
  in `subscription-design.md`.
- **Done when:** A test covers subscribe with an unknown preset, and the client receives a terminal
  rejection.

### AR-07

**Live deltas can precede `subscriptionAccepted` when `sendSnapshot:false`** · Low · Code-read

- **Where:** `WebSocketSessionManager.cs:100` (`snapshotActive: subscribe.SendSnapshot`)
- **Problem:** Without a snapshot, the subscription is never marked as buffering. The worker can publish
  live deltas between finishing the subscribe and the session writing `subscriptionAccepted`.
- **Fix:** Always start a new subscription in buffering mode, and release the buffered frames right after
  `subscriptionAccepted` is written.
- **Done when:** A test that injects a mutation between subscribe completion and acceptance shows
  `A` first.

### AR-08

**Schemas over 128 fields are accepted but unsupported** · Low · Code-read

- **Where:** `src/LiveViewEngine.Core/FieldMask.cs:9` (`WordCapacity = 2`). `CollectionSchema` has no
  validation.
- **Problem:** A create with more than 128 fields succeeds. Writes to later fields, and projections that
  include them, then throw `IndexOutOfRangeException`.
- **Fix:** Validate in `CollectionSchema` (or at create) and fail the create with a clear message. Raising
  the limit is a separate decision, because it affects the size of every `FieldMask` copy.

## Slow clients and snapshots

### AR-09

**No memory bound for slow-but-progressing clients** · High · Code-read

- **Where:** `WebSocketConnection` (bounded channel, per-send `SendStallTimeout`) and
  `WebSocketOutboundOptions.cs:14` (`OutboundQueueCapacity = 2_000_000`)
- **Problem:** The only slow-client detector is a single send making no progress for 30 s. A client that
  drains slower than its update rate never trips it. Its queue grows until 2M frames, each a separate
  `byte[]`. That is roughly 100–300 MB per connection, and nothing conflates or drops intermediate updates.
- **Impact:** A handful of slow consumers (mobile clients, background tabs, congested links) can exhaust
  server memory.
- **Proposed design (needs an intent):**
  - Track queued **bytes** per connection. Increment in `TryWrite`, decrement after each `SendAsync`.
  - **Soft limit, resync mode:** stop enqueueing live deltas for that connection's subscriptions and mark
    them stale. When the queue drains below a low-water mark, re-snapshot each stale subscription's current
    viewport through the worker (reusing the `updateview` / `SnapshotMode.Full` path, with its buffering).
    This is always correct regardless of positions, and bounds memory to roughly one viewport per
    subscription.
  - **Hard limit:** fault the connection, as the capacity check does today.
  - Options: `SoftLimitBytes`, `HardLimitBytes`, `ResyncLowWaterBytes`.
- **Done when:**
  - A test with a slow fake socket shows bounded queued bytes and a final client state equal to a fresh
    snapshot.
  - A load test with throttled consumers shows stable server memory.
  - AR-16 metrics expose resyncs.

### AR-10

**Live-delta coalescer is effectively dead and would reorder positions if enabled** · Medium · Code-read

- **Where:** `src/LiveViewEngine.WebHost/WebSocket/LiveDeltaCoalescer.cs:16`, `:103`; `OutboundFlushPolicy`
- **Problem:**
  - `ViewEngine.PublishMutationResultAsync` calls `FlushAsync` after every mutation, so `PendingLiveDeltas`
    never holds more than one mutation's deltas. The coalescer costs a linear scan per delta and saves
    nothing.
  - If flushing becomes time- or backpressure-based, `MergeRowUpdate` keeps the earlier slot but takes the
    later `Position`. The merged update jumps over the insert/remove/replace deltas in between, which
    shifted positions. The PoC client applies updates by position first (`gridShared.ts:1249`), so it would
    update the wrong row.
  - `RowReplaceDelta` is invisible to `FindPendingRowIndex`.
- **Fix:** Pick one:
  - (a) delete the coalescer and the pending buffer, and write frames directly (simplest, and resolves
    AR-11);
  - (b) keep it as part of AR-09, merging only when no structural delta for the subscription has been
    queued since the pending update, and handling `RowReplaceDelta`.
- **Done when:** The chosen option has tests covering update → insert-above → update sequences.

### AR-11

**`FlushAsync` visits every connection after every mutation** · Medium · Code-read

- **Where:** `src/LiveViewEngine.Core/ViewEngine.cs:193`,
  `src/LiveViewEngine.WebHost/WebSocket/WebSocketOutboundPublisher.cs:287`
- **Problem:** Every mutation, in any collection, iterates all connections and subscriptions, taking each
  connection's lock. The cost is `O(connections × subscriptions)` per ingested row, and it causes lock
  contention between collection workers.
- **Fix:** Resolved by AR-10 (a). Otherwise, record the subscriptions touched in `PublishAsync` and flush
  only those.
- **Done when:** A benchmark with 1k idle connections plus one active subscription shows per-mutation
  cost independent of the number of idle connections.

### AR-12

**Snapshot size is unbounded, built on the worker, and faults above queue capacity** · High · Code-read

- **Where:** `src/LiveViewEngine.Core/Runtime/CollectionRuntime.cs:796-846` (`BuildStreamingSnapshotDeltas`)
  and `WebSocketOutboundOptions.cs:14`
- **Problem:**
  - `pageSize` is optional, so any client can request the whole collection.
  - The snapshot is copied row by row on the collection worker, and that collection's ingest waits
    meanwhile. It is then encoded into the connection queue all at once, so peak memory is about two copies
    of the snapshot per subscriber.
  - A snapshot with more rows than `OutboundQueueCapacity` always faults the connection. That contradicts
    the option's comment ("not sized to any expected … snapshot size").
- **Fix:**
  - Short term: add `LiveViewEngineOptions.MaxPageSize`, which clamps or rejects with
    `page_size_too_large`, and choose how an omitted `pageSize` is handled. Correct the options comment.
  - Longer term: produce snapshot frames incrementally. The worker copies one batch per work item and yields
    between batches, and the rows are kept consistent by holding back live deltas for that subscription
    until the last batch.
- **Done when:** Oversized requests are handled predictably, with a test, and the configuration is
  documented in the README.

### AR-13

**Deltas are encoded once per subscriber, on the collection worker** · Medium · Code-read

- **Where:** `WebSocketOutboundPublisher.PublishAsync` (lines 262-281)
- **Problem:** `MutationPropagator` groups subscribers so the deltas are computed once. But each frame embeds
  `subscriptionId`, so the publisher encodes every delta again for each target, under that connection's
  lock, on the thread that serializes the collection's ingest. With N subscribers on one view, a mutation
  costs N encodes.
- **Fix:** Encode the frame body (everything after `KIND|subId`) once per group and format. For each
  target, write the short header and copy the shared body (or send a two-segment WebSocket message).
- **Done when:** A benchmark shows per-mutation cost for 1/100/1000 subscribers on one view. Frames are
  byte-identical to today's.

### AR-14

**One WebSocket message per row/delta** · Low · Code-read

- **Problem:** A 100k-row snapshot means 100k `SendAsync` calls and 100k or more allocations
  (`ArrayBufferWriter` plus `ToArray` per frame).
- **Fix:** Protocol v2, negotiated per connection or subscription: the drain loop packs the queued frames
  into one newline-separated message, up to a size limit. The client parser splits on `\n` (escape `\n` in
  values). Combine with AR-25.

### AR-15

**Redundant `rowUpdate` after `rowInsert`/`rowReplace` of the same row** · Low · Reproduced

- **Where:** `src/LiveViewEngine.Core/Views/MutationPropagator.cs:508`
- **Evidence:** Moving `t-1` produced `R|1|t-1|1|0|t-1|AAPL|500.00|100` followed by `U|1|t-1|0|^1|500.00|^1`.
- **Problem:** The inserted row is read after the mutation, so it already carries the new values.
- **Fix:** Skip `AddUpdateDelta` when this group already emitted the mutated row via insert or replace
  (`insertRowIndex == mutation.RowIndex`). Update the delta tests and README examples.

### AR-16

**No metrics for outbound queues or slow-client disconnects** · Medium · Code-read

- **Problem:** Queue depth, parked snapshot frames and `Fault` reasons appear only in logs.
- **Fix:** Add:
  - `viewengine.ws.connections` (up-down counter);
  - `viewengine.ws.outbound_queue` (frames and bytes; a histogram sampled on write, or an observable gauge
    with the maximum and total);
  - `viewengine.ws.aborts{reason}`;
  - `viewengine.ws.snapshot_rows`;
  - later, `viewengine.ws.resyncs` (AR-09).

## Architecture and ingest

### AR-17

**Two collection registries, and host code can reach non-thread-safe storage** · Low · Code-read

- **Where:** `ViewEngine._collectionRuntimes` (`ViewEngine.cs:25`, `:389`) and `CollectionStore._collections`.
  Host usage: `WebSocketSessionManager.cs:404`.
- **Problem:** Two sources of truth: `StaleIndexReaperService` uses the store, while routing uses the
  engine's copy. `ICollectionStore.TryGet` also exposes `RowCollection`, which the collection worker owns
  and which isn't thread-safe.
- **Fix:** Route through the store only. Expose a schema-only lookup to hosts (`TryGetSchema` already exists)
  and make `RowCollection`/`CollectionRuntime` access internal.

### AR-18

**TCP ingest: double queueing, `ACK` means queued, comparer mismatch** · Low · Code-read

- **Where:** `src/LiveViewEngine.WebHost/Tcp/TcpIngestRequestDispatcher.cs`
- **Problem:**
  - Each row passes through a bounded TCP channel, then `IngestAsync`, then the unbounded worker channel,
    then a thread-pool continuation. That is two queues and a hop per row.
  - `ACK` is sent when a command is queued, and errors raised later are only logged. This is now documented.
  - The queue dictionary uses `OrdinalIgnoreCase` (line 21) while the store is ordinal, so `Trades` and
    `trades` share an ingest queue even though they are different collections.
- **Fix:**
  - Drain the available items and submit them as one batch work item.
  - Optionally add an `ACK`-after-apply mode.
  - Use an ordinal comparer.

### AR-19

**HTTP ingest has no backpressure** · Low · Code-read

- **Where:** `src/LiveViewEngine.Core/CollectionWorker.cs:18` (`Channel.CreateUnbounded`)
- **Fix:** Make the worker queue bounded (configurable) and return `503`/`429` to HTTP producers when it is
  full. Internal work (subscribes, reaping) must not be starved, so consider a separate priority lane.

### AR-20

**Snapshot enumeration does an `O(log n)` lookup per row** · Low · Code-read

- **Where:** `CollectionRuntime.cs:822` → `SharedView.EnumeratePageIndexes` → `GetFilteredByIndex` per row
- **Fix:** Use the index's bulk `Take` / `TakeReverse` (already used by `GetPageIndexes`) into a pooled buffer
  of `SnapshotBatchSize`.

### AR-21

**Allocating/noisy logging on the snapshot path** · Low · Code-read

- **Where:** `WebSocketOutboundPublisher.cs:326`, `:344`, `:361`
- **Problem:** `LogDebug` per batch boxes its arguments even when disabled. `LogInformation` runs on every
  snapshot start and end.
- **Fix:** Use `[LoggerMessage]` source generation, and downgrade start/complete to `Debug`.

### AR-22

**OpenTelemetry packages have known vulnerabilities** · Medium · Reproduced

- **Where:** `src/LiveViewEngine.WebHost/LiveViewEngine.WebHost.csproj:20-24`
- **Evidence:** Restore reports NU1902 for `OpenTelemetry.Api` 1.14.0 (GHSA-g94r-2vxg-569j) and
  `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.13.1 (GHSA-4625-4j76-fww9, GHSA-mr8r-92fq-pj8p,
  GHSA-q834-8qmm-v933). It also reports NU1603, because `OpenTelemetry.Instrumentation.AspNetCore` 1.13.1
  isn't found and 1.14.0 is resolved instead.
- **Fix:** Bump all OpenTelemetry packages to current patched versions, and consider
  `<TreatWarningsAsErrors>` for NU1902 in CI.

### AR-23

**Dead code** · Low · Code-read

- **Unused in production:**
  - `CollectionRuntime.HandleViewportChange`
  - `CollectionRuntime.HandleChangeViewport`
  - `ChangeViewportCommand`
  - `ChangeViewportRuntimeWork`
  - `SnapshotDelta` (non-streaming; tests only)
  - `SharedView.GetPageIndexes` (tests only)
  - `IOutboundEventFormatter` / `JsonOutboundEventFormatter` (registered in DI, used only by tests)
  - the unused `_command` field in `UnknownSubscriptionRuntimeWork`
- **Fix:** Remove the dead code, or move the test-only helpers into the test projects.

### AR-24

**Naming inconsistencies** · Low · Code-read

- The WebHost namespace is `ViewEngineServer.WebApp.*`, while every other project uses `LiveViewEngine.*`.
- The WebSocket DTO has `fieldPresetId`, which maps to `ViewDefinition.FilterPresetId`
  (`WebSocketSessionManager.cs:347`). It's a filter preset, so the wire name is misleading.
- **Fix:** Rename the namespace, and accept `filterPresetId` on the wire, keeping `fieldPresetId` as an
  alias.

### AR-25

**Compact `P` partial flag is ambiguous** · Low · Code-read

- **Problem:** `P|sub|start|total[|1]|fields…` has an optional partial marker, which can't be told apart from
  a field literally named `1`.
- **Fix:** In protocol v2 (with AR-14), always emit the flag token (`0`/`1`), as `A` does.
