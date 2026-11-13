# 0035 — The frame is enforced by the environment, never by the agent's good will

- **Status**: proposed (the architecture was described to the maintainer on 2026-11-13 and he agreed with it in broad lines; accepted when the pull request that carries it is merged)
- **Date**: 2026-11-13
- **Spec**: D-88 ([FUNCTIONAL.md](../FUNCTIONAL.md), [ARCHITECTURE.md](../ARCHITECTURE.md))

## Context

An agent reads pull requests, changelogs and repositories, which can contain instructions. The maintainer wants the agent to be as free as a developer **inside a frame**, and the frame to hold even if the agent misbehaves.

## Decision

1. The agent **never touches**: the pipelines and the files that decide quality and release; any secret; data outside the mission; anything outside the perimeter. If a pipeline is itself broken it may open a pull request on it and **never merges it**.
2. The frame is enforced in layers, from the outside in: **the forge** (protection rules, code owners on pipeline and quality files, an agent identity that cannot merge a protected file, required checks), **credentials** (one repository, one world, short-lived, never a production secret in the agent's reach), **the mandate**, **the runner** (isolation, forbidden paths, network, a stop that kills the sandbox), **the act gateway**, **checks by code**, **the controller**, **the kill switch**, **the audit trail** (also written outside the host).
3. What the agent reads is **data and never an instruction**; nothing it reads can widen its frame, because the frame is not in its instructions.
4. The agent's identity on the forge is a **GitHub App limited per repository**; its rulesets and code owners are written as code in the repositories' infrastructure code.
5. A migration of a database requires a backup first and a way back; deleting data and handling a secret are never done by the agent alone; an exposed secret is removed from the code and told at once, the rotation is the maintainer's.

## Consequences

- Negative cases in the laboratory (CASE-15) test that the environment refuses, on every change to the runner or the rulesets.
