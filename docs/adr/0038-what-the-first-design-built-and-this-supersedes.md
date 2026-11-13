# 0038 — What the first design built and this one supersedes

- **Status**: proposed (the architecture was described to the maintainer on 2026-11-13 and the maintainer agreed with it in broad lines; accepted when the pull request that carries it is merged)
- **Date**: 2026-11-13
- **Spec**: D-91 ([FUNCTIONAL.md](../FUNCTIONAL.md), [ARCHITECTURE.md](../ARCHITECTURE.md))

## Context

The first verticals built typed tasks, ceilings per row, an agent without a shell and a repair on paper. The maintainer judged the direction wrong for the product the maintainer wants (an agentic tool, not a deterministic one) and asked to start over from the needs (USE-CASES.md, FUNCTIONAL.md, ARCHITECTURE.md).

## Decision

1. **Superseded**: the fixed task kinds and their approval path (ADR 0017, D-65, in what concerns task kinds and the executor's checks of a typed task); the ceilings and steps of a row (ADR 0019 and its row rules, ADR 0027's steps); validating a pull request with a hand-written profile and repairing it on paper (ADR 0024, D-72); the proposal of a diff for a version (the part of ADR 0028 that computes a change).
2. **Kept**: the description of the perimeter and the reading of where a version is written, as context (ADR 0028 without the diff); the approvals with a second factor, the delegations, the signatures and the journal (adapted to mandates); the audit trail, the kill switch, the accounts, the history, the live screens (ADR 0025), the Runner as a process (ADR 0026), the review register (ADR 0022).
3. **Removed in order**: first what is wrong and independent of the rest; then, when the replacement works, the typed-task path. The implementation plan keeps the order.
4. **Kept, and now stated**: the per-application and per-environment powers that are off until enabled (D-10, D-11, D-53). The power in effect for a step is the **intersection of the plan entry, the grant and the mandate**; a standing rule never widens a grant. **ADR 0001 is amended**: the operator's explicit approval is the plan validation or a standing rule (bounded by an expiry, a maximum world, a perimeter, and the second factor to create or widen it); the later mandates are granted by the controller agent (ADR 0032) on facts fetched outside the agent, within what the approval allows.
5. **Superseded in part**: D-21 (the unit of work is the repository, not the application) now reads application, repository and target environment for concurrency, and D-34 (no container where the agent acts on infrastructure) gives way to **declared acts under a release-world mandate** and a platform controller limited to the staging pool.
6. The functional specification [FUNCTIONAL.md](../FUNCTIONAL.md) is the reference; where SPEC.md or an older ADR disagrees, FUNCTIONAL.md wins until the reconciliation is finished.

## Consequences

- ADRs 0017, 0019, 0024 and parts of 0027 and 0028 carry a note that points here.
- No data to migrate: only the sandbox ran the first design.
