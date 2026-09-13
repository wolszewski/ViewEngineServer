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

`ViewEngineUpdateBenchmarks`, `--job short`, all three benchmarks in one process. These benchmarks
are new in this change: every pre-existing benchmark inserts, and inserts take the `IsNew`
short-circuit, so none of them touched the code this change modifies.

| | main | mask in group key | **interned (shipped)** |
|---|---|---|---|
| `Update10k_NonSortField_Unfiltered_1Subscriber` | 25.48 / 25.76 ms | 49.29 ms | 27.74 / 25.98 ms |
| `Update10k_NonSortField_Sorted_10Subscribers` | 31.95 / 36.79 ms | 55.62 ms | 31.70 / 31.91 ms |
| `Update10k_SortField_Sorted_10Subscribers` | 58.11 / 71.86 ms | 92.31 ms | 62.04 / 61.46 ms |
| Allocated (first benchmark) | 18.69 MB | 18.54 MB | 18.08 MB |

Two samples per column where shown. **Conclusion: parity with `main` on time, ~3% less allocated.**
The value of this change is the removed field limit, not throughput — no speedup is claimed.

Caveats on these numbers: `--job short` is noisy (see `main`'s 58.11 vs 71.86 on the same benchmark),
and the benchmarks share engine state across methods, so results depend on execution order. They are
reliable enough to have caught a 2x regression, and to support a parity claim, but not for
distinguishing a few percent.

## Insert path (control)

`Insert10k_Unfiltered_1Subscriber`: main 35.53 ms, branch 34.90 ms — unaffected, as expected.
This was the measurement that localised the regression to the update path.
