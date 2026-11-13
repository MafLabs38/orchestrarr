# 0034 — Staging is a prerequisite per application, proved before any mandate on the code

- **Status**: proposed (the architecture was described to the maintainer on 2026-11-13 and the maintainer agreed with it in broad lines; accepted when the pull request that carries it is merged)
- **Date**: 2026-11-13
- **Spec**: D-87 ([FUNCTIONAL.md](../FUNCTIONAL.md), [ARCHITECTURE.md](../ARCHITECTURE.md))

## Context

Many cases cannot be proved in a lone container: an application of several services needs its database, its other services and its front end. The maintainer asked that every production application exist in staging, and that no mandate on the code be given where staging is not usable.

## Decision

1. Every application has a **staging requirement**: **none** (a blank machine is enough), **dependencies** (declared other services or data), or **full stack** (the whole constellation).
2. Every application that runs in production **must exist in staging**, switched off or created on demand. Third-party applications are included.
3. Staging is **the same infrastructure code as production applied to another inventory**, with **no real secret and no real side effect**.
4. An application has a **staging state**: not defined, defined, provisioned, **baseline proven** (unchanged code passes its tests there, with a revision and a date), stale, broken.
5. A mandate on an application's code is **granted only when its requirement is met**. The **baseline proof is itself a mission**.
6. Staging environments are **created on demand and restored to a clean baseline** after a mission (snapshots), by a **platform controller** whose rights are limited to the pool of staging machines. Capacity (two stagings at once on the first machine) is a constraint the scheduler respects: missions wait for a free slot.
7. Portarr says the **facts** (it exists, it runs); Orchestrarr says the **verdict** only a test gives.
8. The cases are explicit: **requirement none** (a blank machine is enough): the mandate is granted, no baseline needed; **staging not defined**: the application is treated as one with no staging (the rule of CASE-17: it goes on like the others with no net, recorded as an accepted risk and reported as a parity-gap finding); **staging defined but broken or stale**: the mandate is **refused** until the baseline is proved again.
9. The **baseline mission** has no problem and no plan entry: its mandate is issued by the scheduler under a system standing rule (ADR 0031).
10. Orchestrarr stores **only the verdict** (state, revision, date, evidence digest); the facts and dependencies are read from Portarr or the description.

## Consequences

- A large piece of infrastructure work, done application by application.
- The staging machine is a hard dependency of the first mission that touches an application's code.
