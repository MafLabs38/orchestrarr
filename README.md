# Orchestrarr

> Act. A self-hosted orchestrator agent that coordinates worker agents to fix, update and verify, in the spirit of the *arr suite.

**Status: first verticals, read-only.** Orchestrarr reads Portarr's findings and the dependency pull requests from GitHub, and shows them as decision cards. It holds **no write power yet**: nothing merges, deploys or writes to a repository. **Approving a card records a decision** (pull request, commit, steps, approver) in an in-memory queue and audit trail, with a global stop; no launcher takes the approvals, so nothing runs. Approving is off (`Approvals:Mode=Disabled`) unless the sandbox operator is configured. The roadmap is in [docs/SPEC.md](docs/SPEC.md) §8; the founding vision in [docs/VISION.md](docs/VISION.md).

Orchestrarr reads the findings of [Portarr](https://github.com/MafLabs38/portarr) (vulnerable dependencies, outdated images, broken applications), plans the work, delegates it to worker agents, verifies that nothing regressed, and proposes the result as a pull request for a human to approve. Safety and approval gates are core concerns: no direct push, sandboxed workers, least privilege, bounded budgets, full audit trail. It works with Mnemarr, the companion long-term memory.

## Documentation

| Document | Content |
|---|---|
| [docs/VISION.md](docs/VISION.md) | Why, what it must do, relation to Portarr, safety model, open questions |
| [docs/SPEC.md](docs/SPEC.md) | Decisions `D-xx`, features `F-xx`, open questions `Q-xx`, roadmap |
| [docs/TESTING.md](docs/TESTING.md) | Test strategy, lanes, thresholds, the review agents |
| [docs/environment-file.md](docs/environment-file.md) | The standalone description of an environment (proposed, not implemented) |
| [docs/adr/](docs/adr/) | Architecture decision records |
| [CLAUDE.md](CLAUDE.md) | Rules for AI assistants working in this repository |

## Releases

A release is a pull request `develop → main`, merged with a **merge commit** (`main` refuses squash: semantic-release reads the commits of the pull request, [ADR 0009](docs/adr/0009-releases-are-decided-by-semantic-release-from-main.md)). The pipeline then picks the version from the Conventional Commits (`feat` minor, `fix` patch, below 1.0 a breaking change is a minor), tags it, publishes the GitHub release and the image `ghcr.io/maflabs38/orchestrarr:<version>`. The title of every pull request into `develop` is a Conventional Commit and decides the version. The cluster's GitOps repository follows the newest version tag.

## Repository settings

The GitHub repository, its branches, rulesets and labels are declared as code in `repos/orchestrarr.yaml` of `MafLabs38/github-template`: change a setting with a pull request there, never in the GitHub settings. `ci.yml`, `codeql.yml`, `mutation.yml` and `ai-review.yml` (the five advisory review agents) are the pipeline (see [docs/TESTING.md](docs/TESTING.md)). CodeQL runs but does not gate merges: the ruleset waits for CodeQL results, so a skipped CodeQL would block every merge.

Before a push, check that no real infrastructure value slipped in (`gitleaks dir .`).

## Run in the sandbox

Needs the .NET 10 SDK and Node 22.

```bash
# API, with the sample data of samples/ (http://127.0.0.1:7878)
dotnet run --project src/Orchestrarr.Host

# Interface with hot reload (http://127.0.0.1:5178, on the loopback only), proxying /api to the API
cd frontend && yarn install && yarn dev

# Tests and lint (see docs/TESTING.md)
dotnet test --solution Orchestrarr.slnx && dotnet format Orchestrarr.slnx --verify-no-changes
cd frontend && yarn test && yarn lint && yarn stylelint && yarn format:check && yarn typecheck
```

**To use the sandbox from another machine**, forward its port instead of opening it: `ssh -L 5178:127.0.0.1:5178 <host>`, or the port forwarding of your editor, then open `http://localhost:5178`. The sandbox operator has no sign-in, so the host and the interface listen on the loopback only, and the routes that record something refuse anything that did not come from there. Do not start the interface with `--host 0.0.0.0`.

To read a real Portarr instead of the sample, set `Portarr:BaseUrl` and give the key as `Portarr:ApiKey` through the environment (`Portarr__ApiKey`) or `dotnet user-secrets`: the key never goes in a file that is committed. Redirects are not followed, so the key only goes to that address; use https or a private network.

To read the dependency pull requests, give a GitHub token that can **only read** as `GitHub:Token` (`GitHub__Token`, or `dotnet user-secrets`): a fine-grained token with *Pull requests: read*, *Checks: read* and *Commit statuses: read* on the repositories (without the last one, the checks of a repository that reports commit statuses stay unknown). Without a token the screens say the pull requests were not read. The token only goes to the configured API address, which must be https (redirects are not followed). The pull requests of the bots of `GitHub:BotLogins` are read (default: Dependabot and Renovate); a list in the configuration replaces the default. For a demo without GitHub, `Forge:SamplePullRequestsFile` reads a file of the shape of `samples/github-pull-requests.sample.json`.

### API

Under `/api/v1` (anything else there is a 404). Two routes record something, and nothing here runs anything:

| Route | Gives |
|---|---|
| `GET /api/v1/work-items[?refresh=true]` | The work items, with their findings, plan and pull requests |
| `GET /api/v1/work-items/{id}` | One work item (404 if unknown) |
| `GET /api/v1/system/status` | Name, version, the state of the sources (`source`, `forge`) and of approving (`approvals`: mode, operator, global stop) |
| `POST /api/v1/approvals` | Approves a card: `{ "workItemId", "steps", "pullRequests": [{ "number", "headSha" }] }`, the pull requests and commits the card displayed. The server decides the repository, the powers and the approver, and refuses unless what was displayed is what is there now |
| `GET /api/v1/approvals` | The approvals waiting for a launcher, newest first |
| `GET /api/v1/activity` | The audit trail (in memory), newest first |
| `GET /api/v1/safety` | The global stop: `{ "killSwitchEngaged" }` |
| `PUT /api/v1/safety/kill-switch` | The global stop: `{ "engaged": true }`. Engaged, nothing is approved |

Approval answers: `201` recorded; `401` `notSignedIn`; `403` `approvalsDisabled`, `hostNotAllowed`, `notAPerson`, `notAnOperator`; `404` `unknownWorkItem`; `409` `pullRequestsNotRead`, `planChanged`, `alreadyApproved`; `422` `nothingTicked`, `stepNotOffered`, `stepNotGranted`, `stepNotSupported`, `mergeNeedsValidation`, `noPullRequests`; `423` `killSwitchEngaged`; `503` `storeFull`, `auditUnavailable`. A draft pull request is never approved. The global stop answers `401` `notSignedIn` and `403` `approvalsDisabled`, `hostNotAllowed`, `notAnOperator`.

### Configuration

| Key | Default | Meaning |
|---|---|---|
| `Portarr:BaseUrl`, `Portarr:ApiKey` | none | The Portarr to read. Without them nothing is read (the sample file is set only in Development) |
| `Portarr:TimeoutSeconds`, `Portarr:MaxResponseBytes` | 20, 10 MiB | Bounds on a Portarr call |
| `GitHub:Token` | none | Read-only token. Without it the pull requests are "not connected" |
| `GitHub:BotLogins` | Dependabot and Renovate | Authors whose pull requests count as dependency updates |
| `GitHub:ApiUrl`, `GitHub:WebUrl` | github.com | For GitHub Enterprise |
| `GitHub:MaxPullRequests`, `GitHub:MaxPages` | 30, 5 | Bounds on a repository's list |
| `GitHub:CacheSeconds`, `GitHub:TimeoutSeconds`, `GitHub:MaxResponseBytes` | 300, 20, 5 MiB | Cache and bounds |
| `WorkItems:CacheDuration`, `MinRefreshInterval` | 30 s, 5 s | How long the list is kept, and the shortest gap between refreshes |
| `WorkItems:ForgeFirstWait`, `ForgeTimeBudget`, `ForgeParallelism` | 3 s, 60 s, 4 | How the forge is read in the background |
| `Powers:Grants` | none | What the operator grants: a list of `{ "Repository": "owner/name" or "*", "Steps": ["validate", "mergePullRequests"] }`. Off until listed. Checked at startup; outside Development a `*` grant may only cover `validate` |
| `Approvals:Mode`, `Approvals:DevelopmentUser` | `Disabled`, `dev-operator` | `Disabled`: nobody can approve. `Development`: a fixed sandbox operator with no sign-in; **the host will not start** with it outside the Development environment or unless it listens on the loopback, and the routes that record something answer only to a local request (`localhost` or a loopback address, from a client on the loopback) |
| `Approvals:QueueCapacity`, `Approvals:AuditCapacity` | 500, 2000 | How many approvals and trail entries are kept (in memory) before refusing |
| `Fleet:SampleFile`, `Forge:SamplePullRequestsFile` | Development only | Sample data of `samples/`; a configured Portarr or token wins over them |

`dotnet run` runs as Development: it enables the sandbox operator (`dev-operator`) and grants validate and merge on every repository, so the Approve button works. The image runs as Production, where approving is off, and it does not carry `appsettings.Development.json`. It listens on port 8080 as a non-root user: `docker run -p 8080:8080 -e Portarr__BaseUrl=... -e Portarr__ApiKey=... -e GitHub__Token=...`.

> **Security.** The API has **no authentication yet** (SPEC Q-12) and the image listens on all interfaces. It reads and shows the state of your fleet, and the two routes that record something (approve, global stop) answer only to the sandbox operator, which the host refuses outside Development and off the loopback; with the default `Disabled` they answer 403. Never start the image with `ASPNETCORE_ENVIRONMENT=Development`: put it behind an authenticating front (single sign-on, a private network) and do not publish its port.

## License

GPL-3.0 is intended, like the *arr apps; there is no `LICENSE` file yet and the spec does not settle it.
