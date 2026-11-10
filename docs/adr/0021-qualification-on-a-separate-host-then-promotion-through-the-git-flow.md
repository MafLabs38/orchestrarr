# 0021 — Qualification on a separate host, then promotion through the Git flow

- **Status**: proposed (the principle is the maintainer's; the host and the first applications are open)
- **Date**: 2026-11-10
- **Spec**: D-69

## Context

Merging was proposed with no target, and deploying with no safety net: a change was deployed blind. Where a GitOps repository deploys on a merge into `main`, merging **is** deploying, so the gate must come **before** that merge. Things that run "in test" must not run on the production cluster.

## Decision (proposed)

1. **The flow is the Git flow of the repositories.** The bot's pull request goes into `develop`; the image built from `develop` is deployed to a **qualification environment**; the checks run there; when they pass, the **release pull request `develop` to `main`** is made (merged with a merge commit, ADR 0009), semantic-release tags it and the GitOps deploys; a check after the deployment follows, with an automatic rollback.
2. **"Merge" means "merge into `develop`"**, which is not production. Promoting to production is a distinct decision, per application, normally a release rather than one update.
3. **The qualification environment is a separate small host that runs containers** (Compose), not a node of the production cluster. The images are built by the CI and pulled from the registry: the host builds nothing. A small paid VPS can do it if it has about two vCPU and four GB; an application that needs more (a JVM-based identity provider) may not fit.
4. **One environment with a queue to begin with**; one per change is cleaner but heavier.
5. **Qualification levels per application** ([ADR 0020](0020-applications-are-described-in-a-private-configuration-repository.md)): none (no test: promotion remains possible and is marked "not qualified"), health, smoke, end-to-end with synthetic data. Test data and secrets are never the production's.
6. **Start with one or two applications** whose pipeline is already in place, and extend as descriptions are written.

## Open

- Which host, which size, which applications first ([SPEC Q-32](../SPEC.md)).
- What the data and the dependencies of each application need to be qualified (a description question, ADR 0020).
