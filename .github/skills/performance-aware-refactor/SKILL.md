---
name: performance-aware-refactor
description: 'Refactor this project for clearer code while preserving performance-critical behavior, adding or validating tests, and explaining the system when helpful.'
---

# Performance-aware refactor

Use this skill when improving readability in this codebase without regressing the critical performance goals of the project.

## Workflow

1. Understand the code path, responsibility, and intended behavior before editing anything.
2. Identify hot paths, allocations, locks, I/O boundaries, and algorithmic complexity before making changes.
3. Refactor for readability only when it preserves or improves fast-path performance, memory usage, and correctness.
4. Keep the performance requirements as the primary invariant; do not trade a measurable regression for cosmetic cleanup.
5. If a refactor does not improve clarity, safety, or maintainability without cost, explain why it should be left alone.
6. When behavior changes or the fix is non-trivial, add or update tests to cover the affected behavior.
7. When asked to explain the code, describe the purpose, data flow, invariants, and any performance-sensitive parts in plain language.

## Rules

- Prefer small, local refactors over broad rewrites.
- Preserve or improve algorithmic complexity; avoid accidental quadratic behavior or repeated work in loops.
- Do not introduce unnecessary allocations, string churn, boxing, lock contention, or extra I/O in hot paths.
- Keep naming and structure clearer, but never at the expense of throughput, latency, or memory efficiency.
- Favor robust, explicit code that remains easy to reason about without sacrificing a measurable performance target.
- If there is no meaningful improvement to make, say so explicitly and explain the tradeoff.
- When tests are missing for a changed code path, add or extend them if it is practical and relevant.

## Explain the code

When explaining a code path:

- Describe the purpose of the component and the problem it solves.
- Explain the important data flow, inputs, outputs, and invariants.
- Highlight the hot path and any performance-sensitive decision points.
- Note the tradeoffs, constraints, and known risks in the current implementation.
- Summarize why the current structure is appropriate for a performance-critical system.

## Refactor guidance

- Improve readability with local naming, extracted helpers, and clearer structure when those changes do not disturb performance-critical behavior.
- Keep feedback loops short and maintainable, but avoid premature abstraction.
- Preserve null-handling, concurrency guarantees, ordering, and resource lifetimes.
- If profiling or code review shows the current implementation is already the correct performance tradeoff, leave it unchanged and explain why.
