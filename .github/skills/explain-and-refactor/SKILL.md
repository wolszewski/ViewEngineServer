---
name: explain-and-refactor
description: 'Refactor this project for clearer and safer code while keeping performance as the top priority, and explain how the relevant code works when the agent is asked to do so.'
---

# Explain and refactor

Use this skill when improving the codebase in a way that helps future maintainers understand the system, while preserving the performance-sensitive design of this project.

## Core mandate

1. Understand the code path, its responsibility, and the behavior it is meant to preserve before making any change.
2. Identify hot paths, allocations, lock usage, I/O boundaries, and algorithmic complexity before refactoring.
3. Prefer small, local, readable improvements over large rewrites.
4. Performance is the primary constraint. Do not make a refactor more readable if it increases latency, memory pressure, contention, or work done in critical paths.
5. If the code is already optimal for the project constraints, explain why a refactor would be a risk or a net negative and leave it alone.
6. When a refactor changes behavior or affects a non-trivial code path, add or update tests covering the relevant behavior.
7. When asked to explain code, describe the purpose, flow of data, invariants, and critical performance-sensitive decisions in plain language.

## Refactor rules

- Keep naming and structure clearer only when safety and performance are preserved.
- Avoid unnecessary allocations, repeated computation, boxing, string churn, locking, or extra I/O in hot paths.
- Preserve ordering, concurrency guarantees, resource lifetimes, and null-handling semantics.
- Prefer explicit code over clever code when the explicit version is equivalent in performance and easier to reason about.
- Avoid broad abstractions unless they reduce duplication without adding overhead or complexity.
- If nothing can be improved without harming performance, say so and explain the tradeoff clearly.

## Explain the code

When explaining a function, class, pipeline, or subsystem:

- State what problem it solves and why it exists.
- Describe the inputs, outputs, and main state transitions.
- Explain the important invariants and assumptions.
- Highlight critical performance-sensitive decisions, bottlenecks, or concurrency boundaries.
- Connect the local code to the overall system design and data flow.
- Keep the explanation practical and concrete, not abstract or speculative.

## Decision checklist

Before approving a refactor, ask:

- Does this improve readability without harming throughput, latency, or memory use?
- Is the code still correct under concurrency and lifecycle constraints?
- Is the change small enough to be reasoned about and tested?
- Can I explain the behavior clearly to another engineer?
- If not, is the current code already the correct tradeoff for a performance-critical system?

If the answer is no, do not refactor for style alone.
