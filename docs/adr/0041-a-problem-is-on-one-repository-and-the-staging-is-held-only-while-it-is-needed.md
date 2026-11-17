# 0041 — A problem is on one repository, and the staging is held only while it is needed

- **Status**: proposed (decided by the maintainer in the chat on 2026-11-17; accepted when the pull request that carries it is merged)
- **Date**: 2026-11-17
- **Spec**: D-95 ([SPEC.md](../SPEC.md), [FUNCTIONAL.md](../FUNCTIONAL.md) FR-MIS-9, FR-STG-4, FR-STG-8). Relates to [ADR 0031](0031-a-mandate-is-the-signed-permission-for-a-mission.md) and [ADR 0034](0034-staging-is-a-prerequisite-per-application.md).

## Context

The first code of the missions carried a **list** of repositories in the plan, the frame and the certificate, while the dossier of a world names **one commit**: a problem on two repositories could never be deployed, and the check at the crossing was ambiguous (finding R-58-163). The same code also presented the staging as a precondition of **every** mandate, checked once at the start, so that after a release it looked as if it were still held (R-58-183).

## Decision

1. **A problem is on exactly one repository** (the maintainer, 2026-11-17: "the code depends on one repository; we do not treat a whole stack"). If a stack has problems on several repositories, they are as many problems, each with its own approval, mandates and dossiers. The plan entry, the frame of a mission and the certificate name **one** repository, and the control plane, the authority and the runners refuse anything else. The wire format keeps a list of one, so that a later widening would not break the signed documents; nothing may use it for more than one.
2. **The staging is a resource that a job takes and gives back.** It is a condition to **start** a job: the job starts only when a staging is **available** (free, usable, its baseline proven; the platform has two, FR-STG-8). The job takes it, prepares what it needs there and works, and hands over. The controller then grants the next mission (the pull request), and **as soon as the change leaves the staging world the staging is released**, so that another job can use it. The later worlds do not ask for the staging again: they ask for **the proof that was made there** (FR-WLD-14). A release that fails comes back to the staging world as a new push and takes a free staging again.
3. In the code the staging verdict is a precondition of the **staging world's** mandates only; the scheduler that keeps the state *available or taken* of each staging, and makes a job wait for a free one, is the work of the vertical that builds the staging runner.

## Consequences

- No list of repositories to reason about at the crossing, in the gateway or in the dossier; the finding R-58-163 closes by the rule, not by a limit.
- The staging is never held by a problem that is waiting for a pipeline or a merge, which keeps two workshops useful for many problems.
- Until the staging runner exists no real mandate can be issued (ADR 0031 point 7), so the availability state is a documented requirement, not yet a check of the code.
