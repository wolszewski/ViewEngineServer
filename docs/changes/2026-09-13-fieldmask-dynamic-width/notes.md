# Benchmarks and measurements

## Struct layouts (`Unsafe.SizeOf`, .NET 10)

Measured while choosing between a bigger inline buffer and a heap-backed mask. `FastPathGroupKey` is
`(int, int?, FieldMask)`.

| `FieldMask` layout | struct | group key |
|---|---|---|
| inline 2 words (before) | 16 B | 32 B |
| inline 4 words (256 fields) | 40 B | 56 B |
| inline 16 words (1024 fields) | 128 B | 144 B |
| inline 2 + overflow ref (hybrid) | 24 B | 40 B |
| heap `ulong[]` only (chosen) | 8 B | 24 B |

A bigger inline buffer is paid by every mask in every collection regardless of real width, which is
why it was rejected.

## Predicates in isolation

21-field schema, `--job short`:

| | Mean | Allocated |
|---|---|---|
| `ContainsAny`, 1 changed column | 1.02 ns | – |
| `ContainsAny`, 20 changed columns | 1.04 ns | – |
| `GetHashCode` (cached) | 0.22 ns | – |

The predicates are ~1 ns, so they could not account for the +2.35 us/update seen below — which is
what redirected the investigation to the group key.

## Update path, before/after

> **Correction (2026-09-20, after review).** Two claims in this section were wrong, and the
> `ViewEngineUpdateBenchmarks` class the numbers came from has since been deleted; its cases were
> folded into `ViewEngineModifyBenchmarks`, which already had the per-iteration reset this class
> lacked. See the corrected reading below the table.

`ViewEngineUpdateBenchmarks`, `--job short`, all three benchmarks in one process.

| | main | mask in group key | **interned (shipped)** |
|---|---|---|---|
| `Update10k_NonSortField_Unfiltered_1Subscriber` | 25.48 / 25.76 ms | 49.29 ms | 27.74 / 25.98 ms |
| `Update10k_NonSortField_Sorted_10Subscribers` | 31.95 / 36.79 ms | 55.62 ms | 31.70 / 31.91 ms |
| `Update10k_SortField_Sorted_10Subscribers` | 58.11 / 71.86 ms | 92.31 ms | 62.04 / 61.46 ms |
| Allocated (first benchmark) | 18.69 MB | 18.54 MB | 18.08 MB |

Two samples per column where shown.

**Corrected reading.** The two errors:

1. *"every pre-existing benchmark inserts"* was false. `ViewEngineModifyBenchmarks` already ran 10k
   updates across unfiltered/sorted/filtered engines at 1/2/10 subscribers, with 1-5 changed fields
   and a correct `[IterationSetup]` reset. The new class was a weaker duplicate of it.
2. `Update10k_NonSortField_Sorted_10Subscribers` measured nothing. Its engine sorted `date`
   descending with `PageSize = 50`, and the hot keys `O00001`-`O00050` carry seed dates topping out
   at `2024-12-24`, behind the ~119 rows dated `2024-12-28`. Every update was dropped at
   `IsPositionInViewport` before a delta was built, so that row times viewport rejection, not the
   grouping work it is named for. Its `SortField` sibling also wrote `"u{n}"` into the sort column,
   which sorts above every seeded date and pins the touched rows to the top from the first
   invocation onward — so only the unmeasured warm-up iteration saw long-distance reorders.

What survives: `Update10k_NonSortField_Unfiltered_1Subscriber` is sound (no sort column means arrival
order, so `O00001`-`O00050` really are at positions 0-49), and it carries the allocation figure. The
2x regression this section localised was real and is still visible in that row. **The parity claim
rests on that one row, not on all three**, and it has not been re-measured against the replacement
benchmarks.

Caveats that still apply: `--job short` is noisy (see `main`'s 58.11 vs 71.86 on the same benchmark).
Reliable enough to have caught a 2x regression; not for distinguishing a few percent.

## Update path, re-measured against main (2026-09-20)

Run against a `main` worktree with the replacement benchmarks patched in, so both sides execute
byte-identical benchmark code. `--iterationCount 15 --warmupCount 5`, unfiltered/10 subscribers only
(no sort index, to isolate the predicate from reordering work), sequential runs on one machine.

| | main | branch | Δ |
|---|---|---|---|
| `Modify10k_Unfiltered_10Subscribers` (1-5 changed fields) | 26.92 ms | 27.81 ms | +3.3% |
| `Modify10k_AllFields_Unfiltered_10Subscribers` (17 changed fields) | 30.56 ms | 32.03 ms | +4.8% |
| Cost attributable to width (AllFields − control) | +3.64 ms | +4.22 ms | **+0.58 ms** |
| Allocated, control | 11.88 MB | 11.72 MB | −1.3% |
| Allocated, AllFields | 14.02 MB | 13.87 MB | −1.1% |

An earlier `--job short` (3 iterations) attempt was discarded: BenchmarkDotNet reported medians
diverging from means, and the width-attributable cost came out *negative* on the sorted rows, which
is meaningless. Three iterations cannot resolve a difference this size.

**Reading.** The `ContainsAny` retype is measurable but small here. Isolating it: the extra 0.58 ms
spans 10 000 updates × 10 subscribers = 100 000 predicate calls over 14 extra changed columns, i.e.
**~0.4 ns per column per subscriber** — about one cycle, as a linear scan should be.

Because it is linear in `changed columns × subscribers`, it scales with exactly the workload this
change unlocks. Extrapolating that per-column figure to a 200-field record rewrite with 100
subscribers gives ~8 µs per mutation of predicate work, against roughly nothing on `main`'s two
word-ANDs. That case **cannot be measured against `main`** — `main` caps at 128 fields, which is the
defect this change removes — so the extrapolation stands unverified. If "publish the whole record"
producers on wide schemas are expected, this deserves a guard (rebuild the mask once when the changed
count is high) and a wide-schema benchmark to justify it.

**On the parity claim.** At benchmark width the honest statement is parity-to-slightly-slower, not
parity: +3.3% / +4.8%, with allocation ~1% better. Note the +3.3% appears on the *control*, where
`ContainsAny` sees only 1-5 columns and should be roughly a wash against two word-ANDs — so a broad
few-percent cost is more likely the `FieldMask` representation change (`main`'s `[InlineArray]` value
struct became a heap `ulong[]`, adding an indirection to every bit test and a copy to every mask
hand-off) than the predicate. That is a separate question this run does not settle.

## Insert path (control)

`Insert10k_Unfiltered_1Subscriber`: main 35.53 ms, branch 34.90 ms — unaffected, as expected.
This was the measurement that localised the regression to the update path.
