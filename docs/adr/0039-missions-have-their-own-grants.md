# 0039 — Missions have their own grants, per application

- **Status**: proposed (accepted when the pull request that carries it is merged)
- **Date**: 2026-11-15
- **Spec**: D-92 ([ADR 0038](0038-what-the-first-design-built-and-this-supersedes.md) keeps D-10, D-11 and D-53)

## Context

The first design grants powers per repository and per step (`Powers:Grants`, D-53). A mission works in a world for an application, and the power that matters to it is how far it may go: the highest world, read again at every crossing and before every merge or deployment (ADR 0031 point 8). D-11 asks for the deployment to be granted per environment; the platform controller, which knows the environments, does not exist yet.

## Decision

1. **`Missions:Grants:<application>`** is the highest world (`Staging`, `Git` or `Release`) that the maintainer enabled for an application. An application that is not listed has none (D-10). It is read from the configuration at every use, so that a grant removed from the file is gone at the next crossing, act or sweep, and it is compared as written (the identifier is not case-folded, and one that holds the path separator of the configuration names nothing).
2. The power in effect is the **intersection** of the plan entry, the grant and the mandate, which falls within the certificate (ADR 0038 point 4).
3. **`Powers:Grants` is not read by the missions.** It keeps governing the steps of the first design until they are removed (ADR 0038 point 3).
4. For now the release world is **one environment per application**; a grant per environment comes with the platform controller (D-11).

## Consequences

- Two grant settings exist side by side until the first design is removed; the README says which one each reads.
- A typo in an application's name gives no power, which is safe and silent: the screens of the plan will say that an application has no grant.
