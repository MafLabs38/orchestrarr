# 0033 — The agent gateway, and the line between free and gated acts

- **Status**: proposed (the architecture was described to the maintainer on 2026-11-13 and he agreed with it in broad lines; accepted when the pull request that carries it is merged)
- **Date**: 2026-11-13
- **Spec**: D-86 ([FUNCTIONAL.md](../FUNCTIONAL.md), [ARCHITECTURE.md](../ARCHITECTURE.md))

## Context

A developer-agent needs a shell and the tools of a developer to run its own tests. The first design gave the agent no shell and had the runner run a profile of steps written by hand for each repository. That removed the agent's autonomy and made every repository a piece of configuration.

## Decision

1. One abstraction, the **agent gateway**, runs a model in a sandbox: it takes a toolset, a workspace, an input and a budget, and returns a transcript and files. It serves the **worker** (the tools of the world) and the **controller** (no tools).
2. The worker is **free inside its workspace**: it reads, edits, builds, tests, runs requests and a browser, writes its dossier and evidence.
3. The acts that have **consequences outside the workspace are gated**: open a pull request, merge, deploy, roll back, create or restore a staging. They are performed by the runner or the control plane, only if the **mandate lists them and the preconditions hold**, and each is audited.
4. The model credential is the maintainer's subscription token, held outside the workspace, never in a prompt, a log or a repository. The sandbox is a container with no capability, not root, bounded resources and limited network.

## Consequences

- The validation profile per repository and the repair handler are removed: the agent does what a developer does.
- The attack surface is the sandbox and the gateway: ADR 0035 says where the frame is enforced.
