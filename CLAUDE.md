# CLAUDE.md

Guidance for Claude Code in this repository. Coding style and PR review rules live in
`.github/copilot-instructions.md`; architecture and protocol docs live in `docs/` (start with
`docs/system-design.md`).

## Documentation Conventions

- Architecture findings go in `docs/reviews/YYYY-MM-DD-<topic>.md` (see `docs/reviews/README.md`) with a dated
  header, a Summary section, and an index table of findings with stable IDs (`AR-NN`) and a Severity / Status
  column. After publishing, only the Status column changes.
- Non-trivial follow-up work starts with an `intent.md` under `docs/changes/YYYY-MM-DD-<slug>/` (template in
  `docs/changes/README.md`).
- README and docs rewrites must keep existing section anchors intact so external links don't break.

## Shell Environment

- The shell is non-interactive: never use `sudo` or any command that prompts for input. If a tool like `gh` is
  missing, say so and propose a user-run install command instead of attempting it.

## Pull Requests

- Every review or refactor task ends with: commit with a conventional-commit message, push a branch, and open a
  PR via `gh pr create` whose body links the dated findings doc.

## Working Style

- If a user message is ambiguous or under ~5 characters, state your best interpretation and proceed, rather than
  stopping to ask — flag the assumption in one line so it can be corrected.
