# 0033 — The agent gateway, and the line between free and gated acts

- **Status**: proposed (the architecture was described to the maintainer on 2026-11-13 and the maintainer agreed with it in broad lines; accepted when the pull request that carries it is merged)
- **Date**: 2026-11-13
- **Spec**: D-86 ([FUNCTIONAL.md](../FUNCTIONAL.md), [ARCHITECTURE.md](../ARCHITECTURE.md))

## Context

A developer-agent needs a shell and the tools of a developer to run its own tests. The first design gave the agent no shell and had the runner run a profile of steps written by hand for each repository. That removed the agent's autonomy and made every repository a piece of configuration.

## Decision

1. One abstraction, the **agent gateway**, runs a model in a sandbox: it takes a toolset, a workspace, an input and a budget, and returns a transcript and files. It serves the **worker** (the tools of the world) and the **controller** (no tools).
2. The worker is **free inside its workspace**: it reads, edits, builds, tests, runs requests and a browser, writes its dossier and evidence.
3. The acts that have **consequences outside the workspace are gated**: open a pull request, merge, deploy, roll back, create or restore a staging.
   The agent's identity on the forge can **only read and push to non-protected `orchestrarr/*` branches**, without the `workflows` permission; **opening a pull request and merging use another identity held by the runner or the control plane**, after the mandate check. They are performed by the runner or the control plane, only if the **mandate lists them and the preconditions hold**, and each is audited.
4. The model credential is the maintainer's subscription token. It is **never inside a sandbox**: a credential proxy outside the sandbox adds it to the calls to the model provider, and the sandbox holds a dummy or short-lived scoped credential. The sandbox is a container with no capability, not root, bounded resources and limited network (registries only through read-only mirrors or a caching proxy).
5. Containers needed in staging run in a **rootless or throwaway engine** that gives no access to the host; the runner and its keys stay outside the machine the agent controls.
6. The **gateway's holder** is one per sandbox: the runner of its world for the worker, the control plane (through a `controller` runner profile) for the controller.

## Consequences

- The validation profile per repository and the repair handler are removed: the agent does what a developer does.
- The attack surface is the sandbox and the gateway: ADR 0035 says where the frame is enforced.
