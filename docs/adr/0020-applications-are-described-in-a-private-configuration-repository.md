# 0020 — Applications are described in a private configuration repository

- **Status**: accepted by the maintainer on 2026-11-10, as principles. The format is settled by describing two real applications first.
- **Date**: 2026-11-10
- **Spec**: D-68 (complements D-17, D-18, D-20)

## Context

Orchestrarr cannot act on an application whose capabilities it does not know. Portarr says **what runs where**; nothing says **what may be done to an application, how to run it on a test host and how to tell it works**. It must also cover third-party applications (a container manager, an identity provider) whose repository the maintainer does not own, so a file in each application's repository is not enough. This repository is public and generic: nothing real can be written here.

## Decision

1. **A private configuration repository** holds one description per application, read by git through a provider (the same way Portarr's catalog is a private description). It complements D-17 and D-18: Portarr observes what runs where; the description says how to act and test. What Portarr already knows (the workload, the `check`) is not duplicated.
2. **The question that builds the format**: what do I need to install this container on a test host and to check, alone, that it works after a merge? And if it is not tested, what do I still need to know? Five blocks:
   - **source**: where the new version comes from (an application of the maintainer: repository and branch; a third-party one: image and the track followed);
   - **run**: how to start it (image, runtime, ports, volumes, configuration, dependencies, the **names** of the secrets, never their values, and the data: empty or synthetic);
   - **qualify**: how to know it works, as a **mode** (`none`, `health`, `smoke`, `e2e`), the check, and what is *not* verifiable;
   - **promote**: how it reaches production (release pull request and GitOps, a pull on a host, by hand), what to back up first, how to roll back, its criticality and the times when nothing is deployed;
   - **update**: which classes of update (patch, minor, major) may go ahead by themselves, and how far.
3. **Only `id`, `kind` and `source` are required.** A missing field is unknown, never guessed: an application with no description is shown as "not qualified", and the missing description is itself a problem in the table. An agent may propose a first description from the repository; the maintainer reads it.
4. **Each field exists because a step needs it**: the table can say "to qualify, the health check is missing".
5. **The description grants no power.** What Orchestrarr may do is bounded by the executor's own rights list (ADR 0017); the description says what is possible and intended. A change of a description is a **launch contract change**: the maintainer merges it.
6. **The format is not frozen before two real descriptions exist** (one application of the maintainer's, one third-party). The example below is illustrative.

```yaml
id: example-manager
kind: thirdParty              # own | thirdParty
source: { image: example/manager, track: "2.45" }
run: { runtime: docker, ports: [9443], volumes: [manager_data] }
qualify:
  mode: health
  check: { url: /api/system/status, expect: 200 }
  notAvailable: "the Kubernetes environments are not tested"
promote: { method: compose-pull, backup: manager_data, rollback: previous-tag }
update: { autoQualify: [patch] }
```

## First reader

Until the provider that reads the private repository exists, the descriptions are read from one JSON file (`Applications:File`) with the same blocks: `id`, `kind` (`own` or `thirdParty`), `source`, `qualify.mode`, `promote.rollback` and `promote.check`, `update.promote` (the classes that may reach production by themselves). A wrong file stops the host. The format is still not frozen (point 6): `samples/applications.sample.json` is an example.

## Consequences

- A provider reads the private repository and the table reads the descriptions: "goes up to" is computed from them (ADR 0019).
- The environment file (D-20) and Portarr's catalog remain the source of where things run; the descriptions do not replace them.
