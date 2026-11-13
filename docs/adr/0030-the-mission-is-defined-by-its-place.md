# 0030 — The mission is defined by its place: worlds, and a runner with a world profile

- **Status**: proposed (the architecture was described to the maintainer on 2026-11-13 and he agreed with it in broad lines; accepted when the pull request that carries it is merged)
- **Date**: 2026-11-13
- **Spec**: D-83 ([FUNCTIONAL.md](../FUNCTIONAL.md), [ARCHITECTURE.md](../ARCHITECTURE.md))

## Context

The first design cut the work into fixed typed tasks (validate a pull request, repair it on paper, merge it), each with its own code path. It cannot express what the maintainer wants: an agent that does the work of a developer from a problem to production, with the changes of place that a real change goes through (an environment to try it in, the Git pipelines, production).

## Decision

1. **A mission is defined by where it happens, not by what it contains.** The places are three **worlds**: the **staging world** (a workspace in the staging environment, where the application is deployed from the branch, changed, rebuilt and tested), the **Git world** (branch, pull request, CI/CD, merge into the integration branch), and the **release world** (deployment to production, verification, rollback).
2. A mission has an objective, a world, a frame, a budget and a dossier to hand over. It is **not a list of steps**: the agent chooses the path.
3. **The runner is one program configured with a world profile**, which says where it runs, which network it reaches and which tools and credentials the world gives. It knows a mandate, a profile and a workspace; it knows nothing of what a bump or a refactor is.
4. The fixed task kinds of ADR 0017 and the ceilings of ADR 0019 are **superseded** (ADR 0038).

## Consequences

- A new kind of problem needs a case and perhaps a source of findings, not a new workflow in code.
- The runners are few and general; the intelligence is in the agent.
- The Runner of ADR 0026 is kept as a name and as a process; its handlers for typed tasks are removed once the replacement works.
