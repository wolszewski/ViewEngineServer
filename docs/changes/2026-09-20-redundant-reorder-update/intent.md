# Stop emitting a redundant update after a reorder

- **Status:** Done
- **Date:** 2026-09-20
- **Findings:** 2026-09-20 AR-01, AR-02
- **PRs:** #37

## Problem

`ComputePositionDeltas` appends a `RowUpdateDelta` whenever the mutated row's new position falls inside
the viewport (`MutationPropagator.cs:508`). Reaching that line means the position changed — the
position-unchanged case returns early at `:329` — and every branch that can set `newIn` has already
emitted a `RowReplaceDelta` or `RowInsertDelta` built from `SelectRowValues(rowIndex, selectedFieldIndexes)`,
i.e. the complete projected row at post-mutation values.

The trailing update is therefore a strict subset of what was just sent, for the same row at the same
position. Reproduced with a 200-field schema sorting on `f150`, moving `r1` from position 0 to 1:

```text
REPLACE removed=r1@0 insert@1 f150=z f001=r1
UPDATE  row=r1@1 changed=[f150=z]
```

One wasted delta per order-changing mutation per subscriber group, on the same WebSocket path AR-09/AR-10
identify as the memory-pressure seam.

It cannot be removed in isolation. The PoC UI calls `recordLatency` only from `applyUpdate`
(`gridShared.ts:1258`); the positional handlers never sample. Removing the update would take reorder
latency from partly measured to entirely unmeasured (2026-09-20 AR-02).

## Goal

A reorder inside the viewport emits exactly one delta, carrying the row at its new values, and the PoC UI
still reports latency for reordering mutations.

## Non-goals

- The `oldFilteredPos == newFilteredPos` path at `:329`, which correctly emits an update alone.
- The fast path (`CollectFastPathGroups`), which is already update-only and correct.
- AR-10's coalescer work. This change reduces what the coalescer sees but does not fix
  `FindPendingRowIndex`'s blindness to `RowReplaceDelta`.
- Any change to delta *shapes*. No encoder or decoder changes.

## Design

Delete the trailing `if (newIn) { AddUpdateDelta(...) }` block. The preceding branch chain is exhaustive
for `newIn`, so no case loses its row payload:

| Branch | Emits | Row payload source |
|---|---|---|
| `oldIn && newIn` | Replace | `mutation.RowIndex` |
| `oldBefore && newIn` | Replace, or Insert when nothing sits above | `mutation.RowIndex` |
| `!oldIn && !oldBefore && newIn` | Replace, or Insert when the page is short | `mutation.RowIndex` |

`visibleMask` is `FieldMask.From(selectedFieldIndexes)`, so the update's
`FilterChangedColumns(changed, visibleMask)` can never name a field outside the row payload's
`selectedFieldIndexes`. The subset relationship holds for every projection.

In the PoC UI, call `recordLatency` from `applyReplace` and `applyInsert` using the row payload those
deltas already carry, so sampling no longer depends on which delta shape the server picked. This also
closes the pre-existing gap for branches that never emitted an update.

Alternatives rejected:

- *Keep the update and drop the row payload from the positional delta.* The payload is what lets a client
  render a row entering the viewport; the update alone cannot.
- *Emit the update only when the positional delta is an Insert.* Inserts carry the full row too, so this
  narrows the waste without removing it, and leaves two shapes to reason about.

Hot paths: strictly less work — one fewer `ViewDelta` allocation and one fewer `FilterChangedColumns`
call per reorder per group. No new allocation or locking.

## Contract changes

- **Wire protocol:** no shape changes. Clients receive strictly fewer `rowUpdate` frames; a reorder that
  previously produced `rowReplace` + `rowUpdate` now produces `rowReplace` alone. Backward compatible for
  any client that applies the row payload it is given, which both the PoC UI and the C# clients do.
- **Public API / options:** none.

## Acceptance criteria

- [x] A reorder inside the viewport emits exactly one delta, whose row payload carries the new values
- [x] Integration tests pin the delta sequence for each `newIn` branch: move within the page, enter from
      above, enter from below (`ReorderDeltaEmissionTests`)
- [x] The position-unchanged path still emits its update alone
- [x] Existing propagator and blotter tests pass unchanged; only the AR-08 wide-schema reorder test
      changed, back to asserting a single event
- [x] PoC UI records a latency sample for a reordering mutation (`applyReplace` / `applyInsert`)
- [x] `docs/system-design.md` delta-group wording matches the new behaviour
- [ ] Benchmark: `ViewEngineUpdateBenchmarks` sort-field update, before and after — **not run**; the
      change only removes an allocation, so the expected direction is obvious, but the number is unmeasured

## Risks and rollout

- **Cell flash.** The PoC grid sets `enableCellChangeFlash` (`gridShared.ts:80`). A reordered row is
  applied via `applyTransaction({ remove, add })`, which does not flash as a value change, so reordered
  rows will stop flashing green. Cosmetic, and arguably more accurate — the row moved rather than changed
  in place.
- **A client relying on `rowUpdate` to notice a change.** Any such client is already wrong for the
  `newIn: false` branches, which never emitted one. Called out in the PR so client owners can confirm.
- No feature flag. The change only removes a redundant frame, and a flag would mean maintaining both
  emission shapes.
