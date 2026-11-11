# 0024 — A pull request is validated for real in a throwaway container, and an agent may try to repair it on paper

- **Status**: proposed (built in the sandbox on 2026-11-11 at the maintainer's request; accepted when the pull request that carries it is merged)
- **Date**: 2026-11-11
- **Spec**: D-72 (refines D-65 and [ADR 0017](0017-orders-are-typed-tasks-approved-with-a-second-factor-that-the-executor-checks.md); amends [ADR 0023](0023-options-lower-security-only-where-the-host-allows-it-and-the-executor-keeps-its-own-say.md))

## Context

ADR 0017 left the executor as a dry run: it checked everything and started nothing, so nobody could tell whether the chain worked. The sandbox needs a real, bounded run, and the maintainer needs to see an agent work before deciding to give any task a write power.

## Decision

1. **`ValidatePullRequest` runs for real.** The executor fetches the head of the pull request, **refuses it if it is not the commit that was approved** (the fact comes from the forge, not from the task), and runs the steps of the repository's **profile** in a container: no capability, not root, bounded CPU, memory, processes and time, no network except for the steps that ask for it (an install), no credential. The profile (image, steps, commands) is the executor's own configuration. A task never carries a command. A step that fails is a result (`failed`), what prevents the run (no profile, no fetch, a commit that moved, a container that does not start) is a refusal. The end of every step's output goes in the chained trace.
2. **`RepairPullRequest` is a third typed task, chosen, never default.** The table offers "Try to repair" on a pull request whose checks fail, where the `repair` step is granted and an executor accepts the kind. The executor validates first (nothing to repair when it passes), lets an agent (Claude Code, headless, on the subscription) edit the code in a container that holds the token of the subscription and the folder of the run, **refuses a change to a protected file** (tests, dependencies, pipeline), **runs the steps again itself** (what the agent says is not a fact) and puts the **patch** in the trace. The instruction is built from a template; what the failing step printed is placed between markers, as data. The whole work of the agent (what it says, reads, edits and runs) is written in the trace as it happens, so that nothing is a black box.
3. **Nothing is pushed.** Applying a patch (a commit on the branch of the pull request) is a write power that is not built; it needs the maintainer's decision and its own credential (ADR 0017, point 4).
4. **A runner that changes nothing outside its sandbox does not force the code of the second factor.** ADR 0023 let the code be off only while every runner was a dry run; the validation and the repair also change nothing outside a throwaway container, so they may run on an executor that does not ask for the code (development, where the host allows it). A runner that merges, pushes or opens a pull request never may. The default stays safe: the code is asked for.
5. **The trace is sent while the run goes on**, line by line (ADR 0025), not at its end.

## Consequences

- Docker becomes a power of the executor's user (it can start any container): the executor runs as a user meant for that, and the image of the agent is a build file the maintainer merges (`docker/agent/Dockerfile`, version pinned).
- The container of the agent reaches the network (the API of the model, the package registry). That is acceptable for a test application and a reason to restrict its egress before it touches anything private.
- The token of the subscription goes to the container as an environment variable, never in an argument or in the trace.
- The workshop's verdict can differ from the forge's: a pull request that passes here and fails on the forge points at the pipeline (an old Node version, for example), not at the pull request. The table shows both.
