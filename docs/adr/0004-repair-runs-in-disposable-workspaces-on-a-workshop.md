# 0004 — Repairs run in disposable workspaces on a workshop host

- **Status**: accepted for points 1 to 8 (confirmed by the maintainer on 2026-11-03); points 9 to 11 are proposed
- **Date**: 2026-11-03
- **Settles**: SPEC Q-07 (agent runtime), Q-08 (where workers run), most of Q-09 (budgets)

## Context

Validating a dependency pull request needs no new code. **Repairing** does: a vulnerable dependency whose bump breaks the build, an obsolete base image, a failing CI. A worker must then check out the code, change it, build it and test it. None of that may happen in the live tree of a service or of the maintainer, and the machine that does it is small (a build that saturates its CPU freezes it).

Two things are easy to mix up and must stay apart: **where the work is done** (the workshop) and **where an application runs** (its target environments, which Portarr describes, ADR 0002).

## Decision

1. **The worker is Claude Code, run non-interactively, in a container** started for the run (SPEC Q-07).
2. **A workshop is a machine where workers run and build.** It is a concept of the application, separate from the target environments. It has a root directory, a limit of concurrent runs, resource limits and a retention time. The first workshop is the development host itself (a local one); a remote one comes later.
3. **A dedicated root directory**, never under the directories where projects are developed. **One directory per run, with a codified name**: `<root>/<owner>__<repository>/<yyyyMMdd-HHmm>-<work-item-slug>-<run-id8>/`. The run id ties it to the audit trail.
4. **A fresh checkout per run**, from a bare mirror kept per repository (fetched, never worked in) with `git worktree add`, on its own branch `orchestrarr/<work-item-slug>`. Validating an existing pull request checks out its head; **repairing starts a new branch from the integration branch**, because a commit on a bot's branch is overwritten when the bot rebases.
5. **Nothing runs on the host itself.** The container has the workspace as its only writable mount: no home directory, no container-runtime socket, no credentials of the host. Its CPU and memory are limited by the host's resource-limited slice, and one run at a time by default.
6. **The repository credential** is an installation token of a GitHub App, scoped to the one repository (contents and pull requests), minted for the run, passed to the process through its environment, never written to disk, a prompt or a log.
7. **The worker never holds deploy credentials.** It edits code and opens a pull request. Merging and deploying are done by Orchestrarr's own code, with credentials scoped to the one target and the one step the operator ticked (ADR 0001).
8. **Lifecycle**: on success the branch is pushed, the pull request opened and the directory deleted. On failure it is kept for a bounded time (default 24 hours) so that it can be diagnosed, then deleted. A janitor removes orphans and enforces a disk quota. The audit trail and logs live in Orchestrarr's own store, not in the workspace.
9. **The model credential is the maintainer's subscription token** (from `claude setup-token`), not an API key, so that the runs are drawn from the subscription quota (proposed). It is a long-lived secret held by Orchestrarr, handed to the agent process only, never to a log or a prompt.
10. **Budgets are in attempts, turns and minutes, not in money** (proposed): with a subscription there is no price per run, there is a quota shared with the maintainer's own sessions. Defaults: 3 attempts and 20 minutes per run, one run at a time. A run that hits the quota is **paused and retried later**, not reported as failed.
11. **The worker's network is restricted** (proposed) to what it needs (the model provider, the forge, the package registries), because a prompt injection that reads the subscription token could otherwise send it out.

## Consequences

- A repair never touches a live tree, and two runs on a repository never collide.
- The separation between repository credentials (worker) and deploy credentials (Orchestrarr) means a compromised worker can open a bad pull request but cannot deploy.
- If Orchestrarr runs elsewhere than on the workshop host (for example in the production cluster), a small runner on the workshop host takes the jobs by polling, so that the workshop exposes no inbound port. This is not needed while Orchestrarr runs on the development host.
- A GitHub App is a prerequisite of any run that writes.
- Third-party image updates need no workspace when the deployment definition is not in a repository Orchestrarr must edit.
