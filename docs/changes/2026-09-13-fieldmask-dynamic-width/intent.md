# FieldMask: remove the 128-field limit by deleting the per-mutation mask

- **Status:** In progress
- **Date:** 2026-09-13
- **Findings:** 2026-09-12 AR-08
- **PRs:** #36

## Problem

`FieldMask` hardcodes `WordCapacity = 2`, so a schema is silently limited to 128 fields
(`FieldMask.cs:9`). `CollectionSchema` has no validation, so a wider collection is accepted and then
throws `IndexOutOfRangeException` on the first write to a field past index 127, and on any
projection that includes one.

A hard cap is a problem beyond the crash: it forces whoever adds a column to keep a budget in their
head. Raising the constant is not a fix either. `[InlineArray(N)]` bakes `N` words into the struct
layout, so the cost is paid by *every* mask everywhere regardless of the collection's real width.
Measured with `Unsafe.SizeOf` on .NET 10:

| layout | `FieldMask` | `(int, int?, FieldMask)` key |
|---|---|---|
| today, 2 words | 16 B | 32 B |
| 4 words (256 fields) | 40 B | 56 B |
| 16 words (1024 fields) | 128 B | 144 B |

Those keys are `FastPathGroupKey` / `ViewportGroupKey` in `MutationPropagator`, rebuilt per mutation
per subscriber group, so an 8× struct is an 8× hash/compare/store for narrow collections too.

## Goal

Collections of any width work correctly, with no constant for anyone to tune or think about, and the
mutation hot path is no more expensive than before.

## Non-goals

- Pooling or otherwise reducing the remaining per-mutation allocations (`MutationInfo` is a `record`,
  `MapToColumnChanges` allocates an array, and per AR-13/AR-14 each frame allocates per subscriber).
  That needs its own measurement and intent — see *Follow-up*.
- Changing any wire format. This is an internal representation change only.

## Design

The hot-path mask is **redundant**. `MutationInfo` carries both `ChangedColumns` (a sparse list) and
`ChangedMask` (a dense bitset derived from it in `RowCollection.AddOrUpdate:33`). Every use of the
mask is either a single bit test or "does this tiny set intersect that set".

So: **delete `MutationInfo.ChangedMask`**, and keep `FieldMask` only for the two sets that are built
once and live long — `FilterSet.Mask` (per filter set) and `ViewportState.VisibleColumns` (per
subscription). Because it is no longer constructed per mutation, it can be a plain `ulong[]` sized to
the schema: no inline array, no cap, no overflow branching, no hot-path allocation.

Two properties of the existing code make this safe and cheap:

- `MutationPropagator.AnalyzeMutationImpact:146` and `ApplyPositionIndexMutation:92` both return
  early for `isDelete || mutation.IsNew`, so mask intersection only ever runs for **updates to
  existing rows**, which touch a handful of fields. The "full-row insert with 200 changed fields"
  case never reaches it.
- `FilterChangedColumns:659` already uses exactly the replacement pattern — iterate
  `ChangedColumns`, bit-test `visibleMask[fieldIndex]`.

`default(FieldMask)` remains the empty mask (`_words` null), so `FilterSet.None` and
`RowCollection.Delete:86` need no special-casing. Equality compares zero-padded and `GetHashCode`
hashes up to the last non-zero word, so masks of differing widths stay consistent.

### Alternatives rejected

- **Validate and reject wide schemas** (the review's suggestion). Keeps the design flaw; the user
  still has to track a column budget.
- **Bigger inline constant.** Costs every mask everywhere, as measured above, to serve a rare case.
- **Hybrid inline + heap overflow.** Removes the cap, but still builds a mask per mutation, grows the
  struct to 24 B, and adds branching to every operation. Strictly worse than not building the mask.

### `ChangedColumns` is retyped

`MutationInfo.ChangedColumns` goes from `IReadOnlyCollection<KeyValuePair<int, string?>>?` to
`KeyValuePair<int, string?>[]?`. This is required, not cosmetic: enumerating through
`IReadOnlyCollection<T>` allocates a boxed enumerator per call, and the new design iterates the
changed set once per subscriber rather than building one mask per mutation. As a concrete array it
passes as `ReadOnlySpan<...>` and allocates nothing. `MapToColumnChanges` already returns an array,
so this only removes an interface at the edge — and it removes a hidden per-call enumerator
allocation that `FilterChangedColumns` incurs today.

### Projection interning, and the regression that forced it

Making the long-lived mask heap-backed put a GC reference inside `FastPathGroupKey`, which
`CollectFastPathGroups` builds **per subscriber per mutation**. That alone cost ~2x on the update
path — the first cut measured 49.3 ms against main's 25.5 ms on
`Update10k_NonSortField_Unfiltered_1Subscriber`. Inserts were unaffected, which pinned it down: they
take `CollectPositionGroups`, whose `ViewportGroupKey` already carried a reference
(`int[] SelectedFieldIndexes`) and so had nothing to lose. Replacing the key's mask with a plain int
recovered it fully, confirming the cause empirically (the precise mechanism — GC tracing versus the
per-lookup pointer chase into the words array — is not pinned down, and the predicates themselves
measure ~1 ns).

So `CollectionRuntime` interns each distinct projection to an int at subscribe time and
`ViewportState` carries the id, keeping the per-mutation key free of references. Note this interns
the **ordered** `SelectedFieldIndexes`, not the mask: the emitted delta carries one group member's
`SelectedFieldIndexes`, so grouping on the set alone (as `FastPathGroupKey` did) would hand
subscribers a payload ordered for a different projection. Interning on order is therefore stricter
than what it replaces.

A hybrid inline+overflow mask would **not** have avoided this: GC tracing follows the type layout, so
a `ulong[]? _overflow` field makes the key reference-containing even when null for narrow schemas.

`ViewportGroupKey` now uses the id too. It kept the mask and the indexes behind a custom comparer,
which had nothing to lose on the *reference* count but did cost an O(width) walk per comparison:
`HandleSubscribe` builds a distinct mask array per viewport, so the reference shortcut never hit and
identical projections compared word by word — worst on the sparse wide projections this change
enables, and invisible to a 21-field benchmark. Since `ProjectionId` is derived from
`SelectedFieldIndexes` and `VisibleColumns` is `FieldMask.From` of that same array, the id determines
both; the group reads them from `groupViewports[0]`, as it already did.

## Contract changes

- `IPositionIndex.AffectsOrder(in FieldMask)` → `AffectsOrder(ReadOnlySpan<KeyValuePair<int, string?>>)`.
  Internal interface; both implementations are explicit, so the public surface is unchanged and
  `PositionIndexPublicSurfaceTests` (name-based reflection) still passes.
- **`MutationInfo` and `FieldMask`: source- and binary-breaking, accepted.** Both are `public` and the
  README points custom hosts at Core, so calling them "engine-internal in practice" was not a
  compatibility classification. Stating it plainly instead:
  - `MutationInfo` loses `ChangedMask` and retypes `ChangedColumns` from
    `IReadOnlyCollection<KeyValuePair<int, string?>>` to `KeyValuePair<int, string?>[]?` — a change to
    a positional record parameter, so both the constructor and the property signature break.
  - `FieldMask` loses `Intersects`, `Key`, `From(IReadOnlyCollection<...>)` and `ToIndexes()`
    (`ToIndexes` had no caller anywhere in `src/` — dead code not catalogued by AR-23). Gains
    `ContainsAny(ReadOnlySpan<...>)`. `From` now takes the schema's field count, and throws
    `ArgumentOutOfRangeException` for an index at or above it where it previously wrote into a
    padding bit and silently accepted.

  **Decision: no shims, no migration.** The project is pre-release with no external consumers, so the
  break is taken rather than carried. Revisit if Core is ever published.
- No wire-protocol change, so no client or UI app needs updating. `LiveViewEngine.Poc.Ui` is the only
  app that consumes deltas (the `Lightstreamer.*` examples speak their own protocol, and the C# HTTP
  and TCP clients are ingest-only); this change touches no file under `src/examples/` or `WebHost`.

## Acceptance criteria

- [x] A collection with more than 128 fields accepts writes to, and projections of, fields past
      index 127 (`WideSchemaTests`; all four throw `IndexOutOfRangeException` before this change)
- [x] `FieldMaskTests` cover word boundaries (63/64/127/128/255), `default`, `ContainsAny` hit/miss,
      and equality/hashing across differing widths
- [x] Full suite green (381 tests, up from 348)
- [x] Benchmarks before/after with `[MemoryDiagnoser]` — see `notes.md`. Re-measured 2026-09-20
      against a `main` worktree running byte-identical benchmark code at 15 iterations: the update
      path is **parity-to-slightly-slower**, +3.3% at 1-5 changed fields and +4.8% at 17, with
      allocation ~1% lower. The earlier "parity, ~3% less allocated" claim came from a 3-iteration
      run and one benchmark that measured nothing; both are corrected in `notes.md`. No speedup is
      claimed — the value of this change is the removed field limit.
- [x] Docs updated: AR-08 status, `system-design.md` *Known issues*

## Risks and rollout

The propagation predicates are the risk: if `ContainsAny`/`AffectsOrder` returned a wrong answer, a
subscriber would silently miss an update or get a needless one. This is covered by the existing
propagation and blotter integration tests, which exercise sort/filter/viewport behaviour end to end,
plus the new boundary tests. No option or flag — the change is behaviour-preserving for every schema
that works today.

## Follow-up

The pooled struct-of-arrays idea for `ChangedColumns` is deferred. The array escapes further than
expected: `FilterChangedColumns:677` returns the mutation's own array when all columns are visible,
it lands in `RowUpdateDelta.ChangedColumns`, and `WebSocketOutboundPublisher.FlushAsync` skips any
subscription with `IsSnapshotActive` — so those delta objects accumulate across many mutations for a
client mid-snapshot, and returning a pooled buffer at end-of-mutation would corrupt that client's
data. That work should start from a `[MemoryDiagnoser]` profile of the real per-mutation allocation
mix (likely dominated by per-subscriber frame encoding, AR-13/AR-14), under a new finding.
