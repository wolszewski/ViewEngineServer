# Delta emission review — 2026-09-20

| | |
|---|---|
| **Scope** | `MutationPropagator` delta emission on the full-recompute path, and how the PoC UI consumes the resulting events |
| **Baseline** | `fix/ar-08-fieldmask-dynamic-width` @ `2b00310` |
| **Method** | Code reading of `MutationPropagator` and `gridShared.ts`, with the emitted event sequence reproduced against a scratch integration test that printed each delta's payload. |
| **Follow-up docs** | [system-design.md](../system-design.md), [websocket-protocol.md](../websocket-protocol.md) |

Conventions for this folder are in [README.md](README.md). Finding IDs (`AR-NN`) are stable and scoped to
this review. Reference a finding as `2026-09-20 AR-01` in intents, commits and PRs. Update only the
**Status** column below when work lands.

**Verification levels:**

- **Reproduced:** observed on a running server or in a scratch program.
- **Code-read:** follows directly from the code, not measured.

**Severity:**

- **High:** data corruption, a denial of service that clients can trigger, or unbounded memory.
- **Medium:** a wrong or degraded result, or a noticeable waste of resources.
- **Low:** hygiene and clarity.

## Summary

Both findings come from the same seam: `ComputePositionDeltas` appends a `RowUpdateDelta` after the
positional delta whenever the mutated row lands inside the viewport, and the PoC UI hangs behaviour off
that update event rather than off the positional deltas.

The server sends strictly more than it needs to (AR-01), while the client measures strictly less than it
should (AR-02). They point in opposite directions, which is why AR-01 cannot be fixed on its own: removing
the redundant update would silently widen the measurement gap AR-02 already describes. They should be
taken together.

Neither is a correctness defect for a client that applies every delta it receives. A client's final state
is the same today and after the fix.

## Index

| ID | Title | Area | Severity | Verified | Status |
|---|---|---|---|---|---|
| [AR-01](#ar-01) | Reorder emits a redundant `RowUpdateDelta` after the positional delta | Core | Medium | Reproduced | In progress ([intent](../changes/2026-09-20-redundant-reorder-update/intent.md)) |
| [AR-02](#ar-02) | PoC UI samples latency only from `rowUpdate`, so reorder-only deltas go unmeasured | PoC UI | Low | Code-read | In progress ([intent](../changes/2026-09-20-redundant-reorder-update/intent.md)) |

## Findings

### AR-01

**Reorder emits a redundant `RowUpdateDelta` after the positional delta** · Medium · Reproduced

- **Where:** `src/LiveViewEngine.Core/Views/MutationPropagator.cs:508`
- **Problem:** `ComputePositionDeltas` ends with an unconditional trailing update:

  ```csharp
  if (newIn)
  {
      AddUpdateDelta(deltas, viewId, collection, mutation, newFilteredPos - start, ...);
  }
  ```

  Reaching that line means the row's position *changed* — the `oldFilteredPos == newFilteredPos` case
  returns early at `:329` with only an update. Every branch that can set `newIn` (`oldIn && newIn`,
  `oldBefore && newIn`, `!oldIn && !oldBefore && newIn`) emits a `RowReplaceDelta` or `RowInsertDelta`
  built from `mutation.RowIndex` via `SelectRowValues(rowIndex, selectedFieldIndexes)` — the complete
  projected row at its post-mutation values.

  The trailing update therefore carries a strict subset of what the positional delta just delivered, for
  the same row at the same position: `AddUpdateDelta` sends
  `FilterChangedColumns(mutation.ChangedColumns, visibleMask)`, and `visibleMask` is
  `FieldMask.From(selectedFieldIndexes)` — the same field set the row payload already covers.

  Reproduced with a 200-field schema, sorting on `f150`, moving `r1` from position 0 to position 1:

  ```text
  REPLACE removed=r1@0 insert@1 f150=z f001=r1
  UPDATE  row=r1@1 changed=[f150=z]
  ```

  The cost is one extra delta per order-changing mutation per subscriber group, on the WebSocket path
  that AR-09/AR-10 already identify as the memory-pressure seam. It is not a correctness defect: the PoC
  UI's `applyReplace` writes `{ ...replace.row }` wholesale (`gridShared.ts:1366`), so the update is a
  no-op overwrite.

  Present since `MutationPropagator` was introduced (`1389c71`); never deliberately revisited.
- **Fix:** Drop the trailing `AddUpdateDelta` when the positional delta already carried the row. Fixing
  this requires AR-02 to be addressed in the same change, because the PoC UI's latency sampling currently
  rides on that update.
- **Done when:** A reorder inside the viewport emits exactly one delta whose row payload carries the new
  values, an integration test pins the sequence for each `newIn` branch, and the PoC UI still reports
  latency for reordering mutations.

### AR-02

**PoC UI samples latency only from `rowUpdate`, so reorder-only deltas go unmeasured** · Low · Code-read

- **Where:** `src/examples/LiveViewEngine.Poc.Ui/wwwroot/gridShared.ts:1258` (`applyUpdate`), against
  `applyReplace` (`:1330`), `applyInsert` (`:1268`) and `applyRemove`
- **Problem:** `recordLatency(updated.updatedDate)` is called only from `applyUpdate`. The positional
  handlers never record a sample, so any mutation that produces only positional deltas is invisible to the
  latency summary the PoC reports.

  Today that gap is partly masked by AR-01: a reorder that lands inside the viewport also emits the
  redundant update, which is what supplies the sample. But branches where `newIn` is false — a row pushed
  out of the viewport, or one entering from below while another leaves — emit no update and are already
  unmeasured, so the reported latency is biased toward non-reordering updates.

  Removing the redundant update without touching this would widen the gap from "some reorders" to "all
  reorders", turning a partial bias into a systematic one.
- **Fix:** Record the latency sample from the row payload the positional deltas already carry, in
  `applyReplace` and `applyInsert`, so sampling does not depend on which delta shape the server chose.
- **Done when:** A reordering mutation contributes a latency sample with the trailing update removed.
