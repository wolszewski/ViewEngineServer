# AGENTS.md

This project keeps its agent conventions in **[CLAUDE.md](CLAUDE.md)** — documentation layout, shell
constraints, the pull-request rule and working style. Read that file; it is the single source and this
one only points at it.

Two things worth knowing before you start:

- **[docs/backlog.md](docs/backlog.md)** lists every open finding across all reviews, split into ready
  and blocked. Start there to pick up work. It is generated — run `python3 scripts/backlog.py` after
  changing any Status cell, and `python3 scripts/backlog.py --check` to verify it is current.
- **[docs/system-design.md](docs/system-design.md)** is the architecture entry point. Wire formats are in
  [docs/websocket-protocol.md](docs/websocket-protocol.md) and
  [docs/tcp-ingestion-protocol.md](docs/tcp-ingestion-protocol.md).

Coding style and PR review rules live in [.github/copilot-instructions.md](.github/copilot-instructions.md).
