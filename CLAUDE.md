# Orchestrarr

> Self-hosted orchestrator agent that coordinates worker agents to keep a fleet of applications healthy, in the spirit of the *arr suite.

**Project stage: need gathering. No code yet.** The next step is a business-analyst interview with the maintainer (see [docs/VISION.md](docs/VISION.md), "Open questions"). Do not scaffold code, pick an agent runtime or grant any real power before the safety model is written into the spec and accepted.

This file is read automatically by Claude Code. It applies to every task in this repository.

## Read before any task

| Document | When |
|---|---|
| [docs/VISION.md](docs/VISION.md) | Always: why this exists, what it must do, safety model, open questions |
| [docs/SPEC.md](docs/SPEC.md) | Always: frozen decisions `D-xx`, features `F-xx`, open questions `Q-xx` (same format as Portarr's). Draft v0.1 from the first interview |

## Planned tech stack (not yet scaffolded)

Same as Portarr, so the three products share conventions:

- **Backend**: .NET 10 (LTS), C# 14, ASP.NET Core, SignalR, FluentValidation
- **Frontend**: React, TypeScript (strict), Vite, TanStack Query, CSS Modules
- **Tests**: xUnit v3 + NSubstitute + AwesomeAssertions, Vitest + Testing Library, Playwright
- **CI/CD**: GitHub Actions, single multi-arch container image on GHCR
- Agent runtime (Claude Code headless, Agent SDK, Managed Agents…) and where workers run: **undecided**, see the open questions.

## Non-negotiable rules

1. **English only** for code, comments, documentation, commits, issues and PRs. The chat with the maintainer is in French; nothing French is committed except clearly marked quotes in `docs/VISION.md`.
2. **Public repo, generic product.** Never commit real infrastructure values (domain names, IPs, host names, personal project names, repository names of a personal setup, tokens), in code, tests, fixtures, docs or commit messages. Use examples (`homelab.example`, `server-1`, `10.0.0.10`).
3. ***arr patterns first.** A single container, API first (the UI consumes the REST API), providers for agent runtimes and sources of findings, Sonarr-like screens (queue, history, activity). The UI follows the same design rules as Portarr; read Portarr's `docs/DESIGN.md` before writing any UI code.
4. **The constellation** (below): Orchestrarr acts; it does not observe the fleet itself (it reads Portarr's findings) and does not keep long-term knowledge itself (it uses Mnemarr). Integrate through APIs and MCP.
5. **Safety is a core concern, not a feature.** Fixed principles until the spec says otherwise:
   - **No direct push** to a protected branch; every change goes through a pull request.
   - **No autonomy of decision, autonomy of execution** ([ADR 0001](docs/adr/0001-merge-and-deploy-on-operator-approval.md)): every work item starts from an explicit operator approval (the decision card). Once approved, Orchestrarr merges and deploys itself, only the steps the operator left ticked. Deployment needs an automatic rollback.
   - **Isolation**: workers operate in sandboxes or git worktrees, never on the live working tree of a production service.
   - **Least privilege**: each worker gets the narrowest credential and tool set for its task, short-lived where possible; no credential is ever stored in a prompt, a log or a memory.
   - **Verify, then report**: a fix is only proposed after tests and a regression check pass; failures are reported, never hidden.
   - **Budgets and kill switch**: bounded cost, time and concurrency per run, and a global stop.
   - **Full audit trail** of every decision and action.
6. **Sandbox first, PR after acceptance.** Develop each vertical in a local hot-reload sandbox (no commit, push, PR, CI or image). Give the maintainer the URL and what to try, iterate live, and open the PR only once the vertical is accepted. Then complete tests, lint, clean commits.
7. **One vertical = one PR**, with its tests and documentation.
8. **Ask, but build.** When gathering needs, ask open questions (never multiple-choice forms). Once the need is clear, code the vertical and ask only when it truly requires an answer.
9. **Never** commit secrets. Use `dotnet user-secrets` locally and GitHub Secrets in CI.
10. **Decisions** get an ADR in `docs/adr/NNNN-title.md` and a `D-xx` row in the spec.

## The constellation

Three products, each with one job, plugged into each other through their APIs and MCP servers:

| Product | Job | Relation to Orchestrarr |
|---|---|---|
| **Portarr** | See and describe the fleet. Never acts (its D-31) | Source of findings and of the environment description (where each application runs), read through its API / MCP ([ADR 0002](docs/adr/0002-environment-description-is-a-provider.md)) |
| **Mnemarr** | Remember: long-term memory for people and assistants | Runbooks and past outcomes read before acting; results written back |
| **Orchestrarr** (this repo) | Act: orchestrate agents that fix, update and verify | — |

## Git workflow

Branches `main` (stable releases) and `develop` (integration) are protected by GitHub Rulesets: **no direct push, ever**. Everything goes through pull requests.

```text
1. git checkout develop && git checkout -b feature/<issue>-<short-description>
2. Commit with Conventional Commits
3. git push origin feature/<issue>-<short-description>
4. gh pr create --base develop
5. CI green + review -> squash merge into develop
6. When develop is stable -> release PR develop -> main
```

Prefixes: `feature/`, `fix/`, `chore/`, `docs/`. Conventional Commits: `feat(worker): ...`, `fix(queue): ...`, `docs(spec): ...`, `chore(ci): ...`.

The remote does not exist until the `repo/add-mnemarr-orchestratarr` pull request of `github-template` is merged and applied; see the README "Bootstrap" section.
