# 0004 — Repairs run in disposable workspaces on a workshop host

- **Status**: accepted for points 1 to 4, 6 to 10 and 12 (confirmed by the maintainer on 2026-11-03); point 5 is **under revision** (see the end); point 11 is proposed
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
5. **Nothing runs on the host itself.** The container has the workspace as its only writable mount: no home directory, no container-runtime socket, no credentials of the host. Its CPU and memory are limited, and one run at a time by default. The limits are **settings of the workshop**, not constants: the default is three of the four cores and a memory cap, to be adjusted after measuring (the host moved from virtual to physical cores, so its old limit may be too tight). The maintainer's rule of running builds through the host's resource-limited slice stays in force until the maintainer changes it.
6. **The repository credential** is an installation token of a GitHub App, scoped to the one repository (contents and pull requests), minted for the run, passed to the process through its environment, never written to disk, a prompt or a log.
7. **The worker never holds deploy credentials.** It edits code and opens a pull request. Merging and deploying are done by Orchestrarr's own code, with credentials scoped to the one target and the one step the operator ticked (ADR 0001).
8. **Lifecycle**: on success the branch is pushed, the pull request opened and the directory deleted. On failure it is kept for a bounded time (default 24 hours) so that it can be diagnosed, then deleted. A janitor removes orphans and enforces a disk quota. The audit trail and logs live in Orchestrarr's own store, not in the workspace.
9. **The model credential is the maintainer's subscription token** (from `claude setup-token`), not an API key, so that the runs are drawn from the subscription quota. It is a long-lived secret held by Orchestrarr, handed to the agent process only, never to a log or a prompt.
10. **Budgets are in attempts, turns and minutes, not in money**: with a subscription there is no price per run, there is a quota shared with the maintainer's own sessions. Defaults: 3 attempts and 20 minutes per run, one run at a time. A run that hits the quota is **paused and retried later**, not reported as failed.
11. **The worker's network is restricted** (proposed) to what it needs (the model provider, the forge, the package registries), because a prompt injection that reads the subscription token could otherwise send it out.

12. **Every analysis and every fix uses Claude Sonnet**, the model with the best quality for its price for this work, set as a setting (the model, and the amount of reasoning) rather than written in code. The maintainer's plan rarely reaches its quota with it.

## Consequences

- A repair never touches a live tree, and two runs on a repository never collide.
- The separation between repository credentials (worker) and deploy credentials (Orchestrarr) means a compromised worker can open a bad pull request but cannot deploy.
- If Orchestrarr runs elsewhere than on the workshop host (for example in the production cluster), a small runner on the workshop host takes the jobs by polling, so that the workshop exposes no inbound port. This is not needed while Orchestrarr runs on the development host.
- A GitHub App is a prerequisite of any run that writes.
- Third-party image updates need no workspace when the deployment definition is not in a repository Orchestrarr must edit.

## Revision proposed (2026-11-03): two kinds of work, two kinds of isolation

Point 5 says nothing runs on the host. The maintainer observed that a container cannot act on what it must act on (Docker, Kubernetes, a machine), and that an infrastructure-as-code project must be operated as one (Terraform and its own pipeline), a container as a container, an application on bare metal as bare metal. The proposal keeps the isolation where it costs nothing and removes it where it defeats the purpose:

- **Repairing the code** (edit, build, unit tests): the disposable workspace of points 3 and 4 stays. It runs under a dedicated operating-system account with no secret of the maintainer within reach; a container is one way to get that, not a requirement.
- **Acting on an environment** (deploy, test end to end, repair a backup): the agent works **on the environment, as Claude Code would**: it reads the repository, understands how that project is operated, and does what the project itself says. No rule per type of project is written into Orchestrarr (SPEC D-33). Instead of a container, the safety comes from a dedicated operator account per environment, a plan written before acting, an explicit approval for what cannot be undone, budgets and a kill switch (D-34), and a journal kept in the environment and mirrored to Orchestrarr (D-35).
- The credentials of point 7 hold: the worker that edits code never holds deploy credentials; the agent that acts on an environment holds that environment's operator account only.

Not decided: where the agent process runs when it acts on an environment (on the environment, or on the workshop through a session opened on it): SPEC Q-19.
