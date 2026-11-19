# A generated backlog index over the review findings

- **Status:** Done
- **Date:** 2026-11-19
- **Findings:** none (process change). Corrects the status of 2026-09-12 AR-15.
- **PRs:** #38

## Problem

Returning to the project after two months, answering "what was I going to do next?" took reading two
review documents, three intent documents and `git log`. The information was all present and correct;
it was just not aggregated anywhere.

Three concrete defects behind that:

1. **The one index drifted.** `docs/reviews/README.md` carried a hand-written "Open findings" count of
   25 for the 2026-09-12 review. The real number is 21. The count was never updated as #34, #35, #36
   landed, because nothing forced it to be.
2. **The dependency graph existed only as prose.** `## Suggested order` in the architecture review
   encodes real edges — "AR-16 first (so the effect can be measured)", "Resolved by AR-10 (a)",
   "AR-14 together with AR-25 as a protocol v2" — but as a paragraph. Answering "what can I start right
   now, with nothing blocking it" means reading and re-deriving that paragraph every time.
3. **A finding was silently already done.** 2026-09-12 AR-15 and 2026-09-20 AR-01 are the same defect
   at the same line (`MutationPropagator.cs:508`). #37 deleted the block outright, which satisfies
   AR-15's fix, but AR-15 still reads `Open` because nothing recorded that the two findings were
   duplicates. Its "Done when" clause about README examples was genuinely outstanding — the root
   `README.md` still showed `rowReplace` + `rowUpdate` for a reorder.

## Goal

One generated file, `docs/backlog.md`, answers "what is open, in what order, and what is blocked" for
every review at once, and cannot drift from the review documents because it is derived from them.

## Non-goals

- **Adopting a backlog tool** (Backlog.md, beads, beans). All three require findings to become
  one-file-per-item, which means giving up dated immutable reviews. That property is what made it
  possible to say exactly what shipped and when, and it is worth more than the tooling.
- **Changing the review or intent conventions themselves.** Reviews stay immutable and dated; intents
  stay the place where a change's *why* is recorded. This adds a derived read layer over them.
- **Backfilling dependency edges exhaustively.** Only edges the review text already states are
  recorded. Inventing new ones would be a new review's job.

## Design

### Edges live in a separate table, not in the index

The obvious move is a `Blocks` / `Related` column on the index table. Rejected: it widens an
already-6-column table across 25 rows to carry data that only 9 rows have, and it edits every row of a
document whose convention is that only the Status column changes after publication.

Instead each review gets an optional `## Dependencies` section holding only the edges that exist:

```markdown
| From | Edge | To | Note |
|---|---|---|---|
| AR-09 | needs | AR-16 | metrics first, so the effect can be measured |
```

This leaves the index table's existing rows untouched, keeps the edge list short enough to read, and is
still machine-readable. The vocabulary is borrowed from beads, trimmed to what this project has use for:

| Edge | Meaning |
|---|---|
| `needs` | From is blocked until To is resolved |
| `resolves` | Doing From closes To as a side effect |
| `duplicates` | Same defect as To, recorded in a different review |
| `related` | Should be read or done together, but neither blocks the other |

`To` may be a bare ID (`AR-16`, same review) or qualified (`2026-09-20 AR-01`). The generator resolves
bare IDs against the review that contains them, which is what makes the existing per-review ID scoping
safe despite `AR-01` existing in both reviews.

### The generator is deliberately dumb

`scripts/backlog.py` parses the index and dependency tables out of `docs/reviews/*.md` and writes
`docs/backlog.md`. Pure stdlib, no build step, runnable as `python3 scripts/backlog.py`. It takes
`--check` so CI can fail when the committed backlog does not match the reviews, which is the mechanism
that stops the drift described above — a hand-maintained count drifted precisely because nothing
checked it.

It classifies an open finding as **ready** (no unmet `needs`) or **blocked** (named blockers), and
sorts ready items by severity then ID. It does not try to reproduce the review's prose `Suggested
order`; that paragraph carries reasoning the table cannot, and stays as the human narrative.

Alternatives rejected:

- *A dotnet tool in the solution.* The repo is .NET, so this was the consistent choice, but it means a
  project reference, a build, and a slower edit loop for a script that formats a markdown table.
- *Hand-maintaining `docs/backlog.md`.* This is what `reviews/README.md` already tried, and the stale
  count of 25 is the evidence for why it does not hold.

### Why not keep the count in `reviews/README.md`

It is removed rather than regenerated. Two numbers for the same fact is how the drift started, and the
backlog now carries per-review counts with a link.

## Contract changes

- **Wire protocol, public API, options:** none. Documentation and tooling only.
- **`docs/reviews/README.md`:** the immutability rule is amended to say the index's Status column and
  the `## Dependencies` section are the maintained parts of a published review. Finding bodies stay
  immutable. This is a convention change, made explicitly rather than by silent precedent.
- **New `AGENTS.md`** at the repository root, pointing at `CLAUDE.md`, so non-Claude agents find the
  same conventions. `AGENTS.md` is the cross-tool convention; `CLAUDE.md` stays the single source.

## Acceptance criteria

- [x] `python3 scripts/backlog.py` regenerates `docs/backlog.md` from the review documents
- [x] `python3 scripts/backlog.py --check` exits non-zero when the committed backlog is stale, and zero
      when it matches
- [x] The generated backlog separates ready from blocked findings, and names each blocker
- [x] Cross-review IDs resolve correctly: both reviews contain `AR-01`, and `duplicates` on AR-15
      points at `2026-09-20 AR-01`, not at its own review's AR-01
- [x] 2026-09-12 AR-15 is marked `Done (#37)` with the duplicate edge recorded
- [x] The root `README.md` reorder examples, JSON and compact, no longer show the trailing `rowUpdate`
- [x] `docs/reviews/README.md` drops the hand-counted column and documents the amended rule
- [x] Docs updated: root README doc table lists the backlog

## Risks and rollout

- **The generator becomes another thing to maintain.** Mitigated by `--check`: if it stops matching, CI
  says so rather than the file quietly rotting. If it ever becomes a burden, deleting
  `scripts/backlog.py` and `docs/backlog.md` loses nothing — the reviews remain the source of truth.
- **Edges are only as good as what was recorded.** An unrecorded `needs` edge means the backlog calls
  something ready when it is not. This is strictly better than today, where no edges are recorded at
  all, but it is not a guarantee. The intent for any grouped work (the slow-client group) is still
  where real sequencing gets decided.
- No feature flag; nothing executes in the server.
