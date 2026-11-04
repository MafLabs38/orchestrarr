# Orchestrarr

> Self-hosted orchestrator agent that coordinates worker agents to keep a fleet of applications healthy, in the spirit of the *arr suite.

**Project stage: first verticals, read-only.** The safety model is accepted ([ADR 0001](docs/adr/0001-merge-and-deploy-on-operator-approval.md)) and the first vertical reads findings and shows decision cards. Orchestrarr holds **no write power yet**: do not grant a power that merges, deploys or writes to a repository before the maintainer has decided it. The agent runtime is decided (Claude Code, non-interactive, in a container, on a workshop host: SPEC D-24, D-25, ADR 0004).

This file is read automatically by Claude Code. It applies to every task in this repository.

## Read before any task

| Document | When |
|---|---|
| [docs/VISION.md](docs/VISION.md) | Always: why this exists, what it must do, safety model, open questions |
| [docs/TESTING.md](docs/TESTING.md) | Before writing or changing tests, or the pipeline: the strategy, the lanes, the thresholds |
| [docs/SPEC.md](docs/SPEC.md) | Always: frozen decisions `D-xx`, features `F-xx`, open questions `Q-xx` (same format as Portarr's). Draft, from the first interviews |

## Tech stack

Same as Portarr, so the three products share conventions:

- **Backend**: .NET 10 (LTS), C# 14, ASP.NET Core (SignalR and FluentValidation planned, not used yet)
- **Frontend**: React, TypeScript (strict), Vite, TanStack Query, CSS Modules
- **Launcher**: Rust, a separate static binary (`launcher/`, **not built yet**), held to the control plane by shared conformance vectors in `contract/launch/` ([ADR 0014](docs/adr/0014-the-launcher-is-a-rust-binary-held-to-the-control-plane-by-shared-vectors.md)). The control plane stays .NET
- **Tests**: xUnit v3 + NSubstitute + AwesomeAssertions, Vitest + Testing Library, Playwright (planned; see [docs/TESTING.md](docs/TESTING.md)); `cargo test` for the launcher, once it exists
- **Quality**: analyzers as errors and `dotnet format`, `rustfmt`, `clippy` as errors and a dependency audit for the launcher once it exists, ESLint (SonarJS), Stylelint, Prettier, SonarQube, CodeQL, gitleaks, Trivy, Stryker, the five review agents. Same as Portarr, plus the fifth agent (`spec-reviewer`, [ADR 0008](docs/adr/0008-a-fifth-agent-keeps-the-documentation-true.md))
- **CI/CD**: GitHub Actions, a container image on GHCR for the control plane (amd64 now, multi-arch when a target needs it); the launcher, when built, has an image of its own ([ADR 0014](docs/adr/0014-the-launcher-is-a-rust-binary-held-to-the-control-plane-by-shared-vectors.md))
- Agent runtime: Claude Code, non-interactive, in a container on a workshop host (decided, SPEC D-24, D-25, ADR 0004); nothing runs yet.

## Non-negotiable rules

1. **English only** for code, comments, documentation, commits, issues and PRs. The chat with the maintainer is in French; nothing French is committed except clearly marked quotes in `docs/VISION.md`.
2. **Public repo, generic product.** Never commit real infrastructure values (domain names, IPs, host names, personal project names, repository names of a personal setup, tokens), in code, tests, fixtures, docs or commit messages. Use examples (`homelab.example`, `server-1`, `10.0.0.10`).
3. ***arr patterns first.** A single container for the control plane, API first (the UI consumes the REST API), providers for agent runtimes and sources of findings, Sonarr-like screens (queue, history, activity). The UI follows the same design rules as Portarr; read Portarr's `docs/DESIGN.md` before writing any UI code.
4. **The constellation** (below): Orchestrarr acts; it does not observe the fleet itself (it reads Portarr's findings) and does not keep long-term knowledge itself (it uses Mnemarr). Integrate through APIs and MCP.
5. **Safety is a core concern, not a feature.** Fixed principles until the spec says otherwise:
   - **No direct push** to a protected branch; every change goes through a pull request.
   - **No autonomy of decision, autonomy of execution** ([ADR 0001](docs/adr/0001-merge-and-deploy-on-operator-approval.md)): every work item starts from an explicit operator approval (the decision card). Once approved, Orchestrarr merges and deploys itself, only the steps the operator left ticked. Deployment needs an automatic rollback.
   - **Isolation**: workers operate in sandboxes or git worktrees, never on the live working tree of a production service.
   - **Least privilege**: each worker gets the narrowest credential and tool set for its task, short-lived where possible; no credential is ever stored in a prompt, a log or a memory.
   - **Verify, then report**: a fix is only proposed after tests and a regression check pass; failures are reported, never hidden.
   - **Budgets and kill switch**: bounded cost, time and concurrency per run, and a global stop.
   - **Full audit trail** of every decision and action.
6. **Sandbox first, PR after acceptance.** Develop each vertical in a local hot-reload sandbox (no commit, push, PR, CI or image). Run it on the machine Claude Code runs on, never on the production cluster, and give the maintainer an address with a port that opens from their own network (host and port in the chat, never committed), with the accounts to use and what to try; iterate live, and open the PR only once the vertical is accepted. Then complete tests, lint, clean commits.
7. **One vertical = one PR**, with its tests and documentation. Before opening it, run the `review-vertical` skill (the five review agents of `.claude/agents/`: tests, quality, spec, security, architecture) and triage their findings in the PR's Review section.
8. **Ask, but build.** When gathering needs, ask open questions (never multiple-choice forms). Once the need is clear, code the vertical and ask only when it truly requires an answer.
9. **Fix what the editor underlines before committing.** SonarLint is bound to the project on the SonarQube server ([docs/TESTING.md](docs/TESTING.md) §7): read the diagnostics of the files you changed (open in the editor, wait for the analysis) and fix the code smells, in addition to the command-line linters.
10. **Never** commit secrets. Use `dotnet user-secrets` locally and GitHub Secrets in CI.
11. **Decisions** get an ADR in `docs/adr/NNNN-title.md` and a `D-xx` row in the spec.
12. **Work autonomously; interrupt the maintainer only for a real advance.** Never propose an intermediate or technical pull request (CI, dependencies, settings, documentation, scaffolding): prepare it, get it green and merge it yourself where the repository allows. Propose a pull request only when it is a real advance of a vertical, **with a demo** (a sandbox URL and what to try), after iterating until the work is good: no SonarQube or SonarLint finding, no major finding from the review agents, CI fully green. Check the real state (merged? green?) before saying anything. **Exception, followed from now on by the coding agent: never merge a pull request that touches the launch contract files**, the list of which is in [ADR 0015](docs/adr/0015-where-the-launcher-gets-what-its-checks-read.md) §3 (SPEC D-61, proposed, followed as a precaution). The maintainer merges those, whatever their size. Nothing but this instruction enforces it today (`CODEOWNERS` has a single catch-all rule); code owners and a ruleset are proposed in ADR 0015.
13. **The review agents' green light comes before the maintainer sees a pull request. Mandatory, for every vertical.** The five agents of `.github/workflows/ai-review.yml` run when a PR is opened or marked ready, and **again only when the `ai-review` label is added, not on every push**. After the last commit: add the label, wait for the five reports **on that exact head** (each comment says `Commit <sha>`; a report on an older commit does not count), fix what they find, push, label again, and repeat. The PR is green from the agents' side when, on the head commit, no agent reports a blocker, critical, high, major or medium finding that is neither fixed nor declined with its reason written in the PR's Review section, and the architecture verdict is not "no". Only then say that the PR is ready. Never ask the maintainer to validate or merge a PR whose head the five agents have not reviewed; this is what guarantees the quality of each vertical, and it is not skippable.
14. **Once the launcher and the vectors exist (SPEC roadmap row 3a, not built yet), its rules exist twice, and the vectors keep them equal.** The launcher is a Rust binary and the control plane is C# ([ADR 0014](docs/adr/0014-the-launcher-is-a-rust-binary-held-to-the-control-plane-by-shared-vectors.md), [ADR 0015](docs/adr/0015-where-the-launcher-gets-what-its-checks-read.md)). A change to the contract that those ADRs list is **one pull request** that changes the C# code, the Rust code and `contract/launch/` together. Never edit a vector by hand to make a test pass; treat a changed expected outcome as a finding.

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
6. When develop is stable -> release PR develop -> main, merged with a **merge commit, never squashed** (semantic-release reads its commits, [ADR 0009](docs/adr/0009-releases-are-decided-by-semantic-release-from-main.md)); the pipeline then tags the version and the cluster's GitOps deploys it
```

Prefixes: `feature/`, `fix/`, `chore/`, `docs/`. Conventional Commits: `feat(worker): ...`, `fix(queue): ...`, `docs(spec): ...`, `chore(ci): ...`.

The repository settings live in `repos/orchestrarr.yaml` of `github-template`; see the README "Repository settings" section.
