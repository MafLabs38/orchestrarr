# 0036 — Loops are bounded, a failure is told, and a clean stop leaves nothing half done

- **Status**: proposed (the architecture was described to the maintainer on 2026-11-13 and he agreed with it in broad lines; accepted when the pull request that carries it is merged)
- **Date**: 2026-11-13
- **Spec**: D-89 ([FUNCTIONAL.md](../FUNCTIONAL.md), [ARCHITECTURE.md](../ARCHITECTURE.md))

## Context

The maintainer wants the system to iterate (a defect found after a world opens a new mandate to repair it) but not forever.

## Decision

1. A problem found **after** a world (a test in staging, the pipelines, production) opens a **new mandate** to repair it, in the world where it can be repaired.
2. In production, a failing verification **rolls back first**, then repairs.
3. The loop is **bounded per problem**: a maximum number of mandates and a maximum time; on reaching it the work stops and the maintainer is told.
4. A mission that cannot finish in its budget **stops cleanly**: nothing half-merged or half-deployed, the branch and notes kept, a report of what was tried.
5. A failure is never hidden: it is in the history and the report, and in a notification when it matters. The maintainer may take a failed problem again with a new validation; it is not retried forever.

## Consequences

- Budgets are in attempts, turns and minutes, not money: a quota that is used up pauses and resumes.
