# Orchestrarr — Vision

> **Status**: inception. Gathered from the maintainer's notes on 2026-10-31; to be confirmed and refined in the first interview.
> This document will feed `docs/SPEC.md`.

## 1. Pitch

**Orchestrarr** is a self-hosted **orchestrator agent with real powers**. It coordinates **worker agents** to fix vulnerable dependencies, update containers and repair applications, then **verifies that nothing regressed** before proposing the change to a human. It reads the findings of [Portarr](https://github.com/MafLabs38/portarr) (Maintenance, Data, Infrastructure) and acts on them; Portarr itself never acts.

It is a standalone, API-first application in the spirit of the *arr suite: one container, pluggable providers, a queue/history/activity interface.

## 2. Why

### Maintainer's words

> « J'ai de plus en plus d'applications. Portarr me montre beaucoup de vulnérabilités et d'images obsolètes, mais c'est à moi de tout corriger à la main. »
>
> « La maintenance doit s'automatiser : un agent qui corrige, teste, vérifie qu'il n'y a pas de régression, et m'ouvre une PR que je valide. »

*(Paraphrased from the maintainer's notes; the exact wording is to be confirmed.)*

### The problem, in English

1. **The number of applications keeps growing**, and so does the maintenance load: dependency vulnerabilities, outdated or vulnerable container images, runtimes reaching end of life, applications that break after an upgrade.
2. **Seeing is solved, acting is not.** Portarr surfaces the findings, by design without acting on the infrastructure. The operator is left with a long list and no time.
3. **Maintenance is repetitive and verifiable**, which makes it a good fit for agents: bump, build, test, compare behaviour, open a pull request. A human should only decide, not execute.

## 3. What it must do

Draft capabilities, to be prioritised in the interview:

- **Intake**: read findings from Portarr (API / MCP), de-duplicate them, group them into work items and rank them by severity and effort.
- **Orchestrate**: an orchestrator agent plans each work item and delegates to **worker agents** (dependency fix, image update, application repair, regression verification). It tracks progress, retries within budget, and stops when stuck.
- **Fix, test, verify, propose**: each worker works in isolation, runs the project's own tests, and a verification step checks there is no regression (tests, health checks, behaviour comparison). The result is a **pull request for human approval**, with evidence attached.
- **Approval gates**: every action beyond opening a pull request needs an explicit human decision, per work item or under a stated policy.
- **Queue, history and activity** screens in *arr style: what is planned, running, waiting for approval, done, failed; full audit trail with logs.
- **Learn**: write outcomes and runbooks to Mnemarr, read them before acting.
- **Standalone first**: works with a manual list of work items when no Portarr is connected.

Out of scope (to confirm): observing the fleet (Portarr), storing long-term knowledge (Mnemarr), being a CI system or a general workflow engine.

## 4. Relation to Portarr and Mnemarr

- **Portarr (See)** is the source of findings. It never acts (its decision D-31); Orchestrarr is the separate brick that does, plugged into Portarr's API. The link is one-way for data (findings in) with an optional status callback so Portarr can show "a fix is in progress".
- **Mnemarr (Remember)** is the memory: runbooks, past decisions and outcomes. Workers read it for context and write back what they did.
- **Orchestrarr (Act)** owns the powers and therefore the safety model below.

## 5. Safety model (draft principles)

> Principles 1 and 2 are amended by [ADR 0001](adr/0001-merge-and-deploy-on-operator-approval.md): after the operator's approval on a decision card, Orchestrarr merges and deploys itself.

1. **No direct push.** Workers push to a feature branch and open a pull request; protected branches are never written to. A human merges.
2. **Human approval gates.** Pull requests are reviewed and merged by a person; deployments, restarts and any change to a running service are explicit approvals.
3. **Sandboxes and worktrees.** Each worker runs in an isolated environment (separate git worktree, container, or ephemeral VM), never in the live tree of a production service.
4. **Least privilege.** Per-worker credentials scoped to one repository and one task, short-lived, never visible to the model beyond what it needs; no secrets in prompts, logs or memories.
5. **Verify before proposing.** A change is only proposed if the build and the tests pass and the regression check is clean; evidence is attached to the pull request.
6. **Budgets and limits.** Maximum cost, time, tokens, concurrency and retries per work item and per day; automatic stop when exceeded.
7. **Kill switch and dry-run.** A global stop, and a mode where agents plan and report without changing anything.
8. **Audit.** Every prompt, tool call, decision and approval is recorded and reviewable.
9. **Untrusted input.** Findings, issue text, dependency changelogs and repository content are treated as data, never as instructions (prompt injection).

## 6. Open questions for the first interview

Asked openly, one topic at a time.

1. **Agent runtime.** Claude Code headless, the Agent SDK, Managed Agents, or a provider abstraction so several can coexist? What are the trade-offs in control, cost, offline use and data leaving the host?
2. **Where workers run.** On the same host as Orchestrarr, in containers, on a dedicated build machine, or in an ephemeral cloud sandbox? How is isolation enforced, and how are builds kept from saturating a small host?
3. **Budget limits.** Cost and token ceilings per work item, per day, per month? What happens at the limit — stop, ask, or degrade?
4. **Which repositories and which targets.** Which source repositories may be touched (allow-list)? Only application code, or also infrastructure-as-code and container definitions? Which forge (GitHub first)?
5. **Which fixes are in scope for v1.** Dependency bumps only? Container image updates? Repairing a broken application? Where is the line between "fix" and "redesign"?
6. **Regression verification.** What counts as "no regression": the project's own tests, health checks before and after, a synthetic smoke test, a comparison of behaviour? What if a project has no tests?
7. **Approval policy.** Always a human per pull request, or auto-merge for low-risk classes (patch bumps with green checks) once trust is built? Who is notified, where (email, push notification, chat)?
8. **Relation to Portarr.** Pull findings periodically or receive a webhook? Does Portarr display the state of Orchestrarr's work items?
9. **Failure handling.** What does a failed attempt look like to the operator? Retry policy, escalation, and how a failed attempt is written to Mnemarr.
10. **Scope check.** The name is settled (Orchestrarr, shorter than the original working name). Anything explicitly out of scope for v1?
