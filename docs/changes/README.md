# Changes

Intent documents for non-trivial work, written **before** implementation. An intent records why a change
exists and what "done" means, so the PR, the tests and later readers can be checked against it.

## When to write one

| Change | Process |
|---|---|
| Small, local fix with an obvious test (for example `2026-09-12 AR-01`) | No intent. The PR description references the finding ID. |
| Touches a contract (wire protocol, public API, options), crosses components, or has design choices | Write `intent.md` first. |
| Several findings solved together (for example the slow-client work: AR-09/10/11/12/16) | One intent that lists all of them. |

## Layout

```text
docs/changes/
└── YYYY-MM-DD-<slug>/
    ├── intent.md     required: problem, goal, acceptance criteria, design
    └── notes.md      optional: benchmarks, experiments, rejected alternatives
```

Lifecycle:

1. Create the intent.
2. Open a PR for the intent, or commit it together with the first implementation commit.
3. Implement against the acceptance criteria.
4. Set `Status: Done` in the intent. Update the review's status column and the living docs.

Intents stay in the repository as the record of why the code looks the way it does.

## `intent.md` template

```markdown
# <Title>

- **Status:** Draft | Accepted | In progress | Done | Abandoned
- **Date:** YYYY-MM-DD
- **Findings:** 2026-09-12 AR-09, AR-12   <!-- or "none" -->
- **PRs:** #

## Problem
What is wrong or missing today, with evidence (numbers, repro, file:line).

## Goal
The observable outcome. One or two sentences.

## Non-goals
What is explicitly out of scope, to stop scope creep.

## Design
The chosen approach, and the alternatives considered and why they were rejected. Call out effects on hot
paths (allocations, locks, work on the collection worker), since performance is the project's primary
constraint.

## Contract changes
Wire protocol, public API, configuration options and defaults. Say whether each is backward compatible.

## Acceptance criteria
- [ ] Testable statements, each covered by a unit, integration or load test
- [ ] Benchmarks for touched hot paths: before and after
- [ ] Docs updated (system-design, websocket-protocol, README)

## Risks and rollout
Failure modes, feature flags or options, and migration for existing clients.
```
