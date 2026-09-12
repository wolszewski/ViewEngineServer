# Reviews

Point-in-time reviews of the codebase: architecture, performance, security. They form the backlog of
findings that future work draws from.

## Conventions

- **File name:** `YYYY-MM-DD-<topic>.md` (for example `2026-09-12-architecture-review.md`), dated on the day
  of the review.
- **Record the baseline** (branch and commit) and how each finding was verified (reproduced or code-read).
- **Stable IDs:** each finding gets an ID scoped to its review (`AR-01`, …). Refer to one as
  `<date> <ID>`, for example `2026-09-12 AR-05`, in intents, commit messages and PRs.
- **Reviews are immutable.** After publishing, only update the **Status** column of the index: `Open`,
  `In progress (<link to intent/PR>)`, `Done (#PR)`, or `Won't fix (<reason>)`. If a finding turns out to be
  wrong, mark it `Won't fix (invalid)` instead of deleting it. New observations go into a new review.
- **Shipped fixes** must also update the living docs (`docs/system-design.md` *Known issues*,
  `docs/websocket-protocol.md`, the README) so those always describe the current state.

## Index

| Date | Review | Open findings |
|---|---|---|
| 2026-09-12 | [Architecture review](2026-09-12-architecture-review.md) | 25 |

Turning a finding into work is described in [../changes/README.md](../changes/README.md).
