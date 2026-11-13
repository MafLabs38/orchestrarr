# 0035 — The frame is enforced by the environment, never by the agent's good will

- **Status**: proposed (the architecture was described to the maintainer on 2026-11-13 and the maintainer agreed with it in broad lines; accepted when the pull request that carries it is merged)
- **Date**: 2026-11-13
- **Spec**: D-88 ([FUNCTIONAL.md](../FUNCTIONAL.md), [ARCHITECTURE.md](../ARCHITECTURE.md))

## Context

An agent reads pull requests, changelogs and repositories, which can contain instructions. The maintainer wants the agent to be as free as a developer **inside a frame**, and the frame to hold even if the agent misbehaves.

## Decision

1. The agent **never touches**: the pipelines and the files that decide quality and release; any secret; data outside the mission; anything outside the perimeter. If a pipeline is itself broken it hands the maintainer a **patch or an issue** and **never pushes or merges it**.
2. The frame is enforced in layers, from the outside in: **the forge** (protection rules, code owners on pipeline and quality files, an agent identity that cannot merge a protected file, required checks), **credentials** (one repository, one world, short-lived, never a production secret in the agent's reach), **the mandate**, **the runner** (isolation, forbidden paths, network, a stop that kills the sandbox), **the act gateway**, **checks by code**, **the controller**, **the kill switch**, **the audit trail** (also written outside the host).
3. What the agent reads is **data and never an instruction**; nothing it reads can widen its frame, because the frame is not in its instructions.
4. The agent's identity on the forge is a **GitHub App limited per repository, without the `workflows` permission** (the forge refuses a push that touches a pipeline file), and it can only read and push to non-protected `orchestrarr/*` branches. **Opening a pull request and merging use a separate identity** held by the runner or the control plane. Jobs on branches written by the agent run **without secrets** (secrets behind environments with a required reviewer). Its rulesets and code owners are written as code in the repositories' infrastructure code.
5. Requests to production from the release world go through an **egress proxy that allows safe methods only** and holds no production session; a verification that must write is a declared act.
6. A migration of a database requires a backup first and a way back; deleting data and handling a secret are never done by the agent alone; an exposed secret is removed from the code and told at once, the rotation is the maintainer's.

## Consequences

- Negative cases in the laboratory (CASE-15, with its variants: the model token, a workflow that exfiltrates a CI secret, instructions for the controller in the dossier, a container reaching the host) test that the environment refuses, on every change to the runner or the rulesets.
- **Open, for the maintainer (SPEC Q-29)**: how a merge that deploys by itself is handled. A proposal: the pipeline of the integration branch holds no deployment secret, deployment is bound to the release branch, and merging into it is a release-world gated act. It is **not settled**: it conflicts with the digest rule (the artifact built from the release branch is not the one proved in staging unless it is promoted by digest), a merge brings the agent's code into the release pipeline, and a repository that deploys itself on a merge to the integration branch would have to be outside the perimeter. Until it is decided, such repositories are outside the perimeter. Also open: the agent's forge identity must not be able to create tags or releases (a tag ruleset), the required checks need tokens that agent branches must not read (the quality gate versus no secrets on agent branches), and the repositories that define the frame (rulesets, code owners, pipeline definitions) are outside the agent's perimeter.
