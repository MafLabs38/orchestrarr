# Orchestrarr

> Act. A self-hosted orchestrator agent that coordinates worker agents to fix, update and verify, in the spirit of the *arr suite.

**Status: first verticals, read-only.** Orchestrarr reads Portarr's findings and the dependency pull requests from GitHub, and shows them as decision cards. It holds **no write power yet**: nothing merges, deploys or writes to a repository. **Approving a card records a decision** (pull request, commit, steps, approver) in a queue and an audit trail, with a global stop, kept in memory or, with `Storage:Provider=Sqlite` (the image's default), in a file that survives a restart; no launcher takes the approvals, so nothing runs. Approving is off (`Approvals:Mode=Disabled`) unless the sandbox operator (`Development`, loopback only) or the sign-in through an identity provider (`OpenIdConnect`, members of the operator group only) is configured. The roadmap is in [docs/SPEC.md](docs/SPEC.md) §8; the founding vision in [docs/VISION.md](docs/VISION.md); how an order is secured, from the person to the machine, in [docs/security-model.md](docs/security-model.md).

Orchestrarr reads the findings of [Portarr](https://github.com/MafLabs38/portarr) (vulnerable dependencies, outdated images, broken applications), plans the work, delegates it to worker agents, verifies that nothing regressed, and proposes the result as a pull request for a human to approve. Safety and approval gates are core concerns: no direct push, sandboxed workers, least privilege, bounded budgets, full audit trail. It works with Mnemarr, the companion long-term memory.

## Documentation

| Document | Content |
|---|---|
| [docs/VISION.md](docs/VISION.md) | Why, what it must do, relation to Portarr, safety model, open questions |
| [docs/SPEC.md](docs/SPEC.md) | Decisions `D-xx`, features `F-xx`, open questions `Q-xx`, roadmap |
| [docs/security-model.md](docs/security-model.md) | How an order is secured, from the person to the machine, with diagrams |
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

# Interface with hot reload (http://127.0.0.1:5178, on the loopback only), proxying /api and /auth to the API
cd frontend && yarn install && yarn dev

# Tests and lint (see docs/TESTING.md)
dotnet test --solution Orchestrarr.slnx && dotnet format Orchestrarr.slnx --verify-no-changes
cd frontend && yarn test && yarn lint && yarn stylelint && yarn format:check && yarn typecheck
```

**To use the sandbox from another machine**, forward its port instead of opening it: `ssh -L 5178:127.0.0.1:5178 <host>`, or the port forwarding of your editor, then open `http://localhost:5178`. The sandbox operator has no sign-in, so the host and the interface listen on the loopback only, and the routes that record something refuse anything that did not come from there. Do not start the interface with `--host 0.0.0.0`.

**To try the sign-in** with a throwaway identity provider, start the one of `samples/keycloak` (`docker compose up -d`, port 8081 on the loopback, realm `orchestrarr` with the operator `alice`/`alice` and `bob`/`bob`, who is not one), then run the host and the interface with the sign-in on and open `http://localhost:5178` (forward 5178 and 8081 if you are on another machine):

```bash
Approvals__Mode=OpenIdConnect SignIn__Authority=http://localhost:8081/realms/orchestrarr SignIn__ClientId=orchestrarr \
  SignIn__ClientSecret=sandbox-only-client-secret SignIn__PublicUrl=http://localhost:5178 SignIn__OperatorGroup=/orchestrarr-operators \
  dotnet run --project src/Orchestrarr.Host
```

That secret is public, written in the sample realm for the sandbox: a real one goes through the environment or `dotnet user-secrets`, never a command line you keep. Keep `SignIn__PublicUrl=http://localhost:5178` as written (the sample realm only accepts that redirect address) and start the interface (`yarn dev`) as usual.

To read a real Portarr instead of the sample, set `Portarr:BaseUrl` and give the key as `Portarr:ApiKey` through the environment (`Portarr__ApiKey`) or `dotnet user-secrets`: the key never goes in a file that is committed. Redirects are not followed, so the key only goes to that address; use https or a private network.

To read the dependency pull requests, give a GitHub token that can **only read** as `GitHub:Token` (`GitHub__Token`, or `dotnet user-secrets`): a fine-grained token with *Pull requests: read*, *Checks: read* and *Commit statuses: read* on the repositories (without the last one, the checks of a repository that reports commit statuses stay unknown). Without a token the screens say the pull requests were not read. The token only goes to the configured API address, which must be https (redirects are not followed). The pull requests of the bots of `GitHub:BotLogins` are read (default: Dependabot and Renovate); a list in the configuration replaces the default. For a demo without GitHub, `Forge:SamplePullRequestsFile` reads a file of the shape of `samples/github-pull-requests.sample.json`.

### API

Under `/api/v1` (anything else there is a 404, or a 401 without a session when the sign-in is on). The sign-in's own routes are under `/auth` (`me`, `login` and `logout` below, and `callback` and `signed-out`, which only the identity provider's redirects use; any other `/auth/...` is a 404); `GET /health` answers `{ "status": "ok" }` to a probe, with no session. Every answer forbids being framed by another page and being sniffed, carries a policy that keeps the interface's scripts, styles and requests to itself (`Content-Security-Policy`); with the sign-in on and an https `SignIn:PublicUrl`, it also carries `Strict-Transport-Security`. Two routes record something, and nothing here runs anything:

| Route | Gives |
|---|---|
| `GET /api/v1/work-items[?refresh=true]` | The work items, with their findings, plan and pull requests |
| `GET /api/v1/work-items/{id}` | One work item (404 if unknown) |
| `GET /api/v1/system/status` | Name, version, the state of the sources (`source`, `forge`) and of approving (`approvals`: `mode` (`disabled`, `development` or `openidconnect`), `enabled`, the identifier `operator` and the name to show `operatorName`, `isOperator` (whether this person may approve and stop), `killSwitchEngaged`, `killSwitchSaved`) and where things are kept (`storage.kind`, `sqlite` or `memory`) |
| `POST /api/v1/approvals` | Approves a card: `{ "workItemId", "steps", "pullRequests": [{ "number", "headSha" }] }`, the pull requests and commits the card displayed. The server decides the repository, the powers and the approver, and refuses unless what was displayed is what is there now |
| `GET /api/v1/approvals?limit=` | The approvals waiting for a launcher, newest first: 200 by default, 1,000 at most; a `limit` outside 1 to 1,000 is brought back into that range, and if the file cannot be read the route answers `503` `storageUnavailable`, never an empty list |
| `GET /api/v1/activity?limit=` | The audit trail, newest first: 200 by default, 1,000 at most, with the same bound and the same `503`. The `id` of an entry is its number in the trail, counted from 1 in the order it was recorded: it never changes |
| `GET /auth/me` | Who is signed in, for any session, even one the API refuses: `{ name, canRead, isOperator }`; `name` is the username, else the full name (each cut to 100 characters), else the subject, else `null` (the interface then says "an unknown account"; such a session cannot read). Only when `Approvals:Mode=OpenIdConnect` |
| `GET /auth/login?returnUrl=` | Sends the browser to the identity provider, then back to `returnUrl` (a path of this application, otherwise `/`). Only when `Approvals:Mode=OpenIdConnect`. If the provider cannot be reached, or the sign-in fails, the browser is sent to `/?signInFailed=1` and the interface says so (the detail stays in the host's log) |
| `POST /auth/logout` | Ends the session and sends the browser to the provider's end-session page. A POST, sent as a form, from the application's own origin |
| `GET /api/v1/safety` | The global stop: `{ "killSwitchEngaged", "saved" }`; `saved` is false when it is engaged but the file could not take it (a restart would release it) |
| `PUT /api/v1/safety/kill-switch` | The global stop: `{ "engaged": true }`. Engaged, nothing is approved. Engaging always takes effect; a release is answered `503` `killSwitchUnavailable` when it cannot be saved, or `503` `auditUnavailable` when it cannot be written to the trail, and the stop stays engaged |

Approval answers: `201` recorded; `401` `notSignedIn`; `403` `approvalsDisabled`, `hostNotAllowed`, `originNotAllowed`, `notAPerson`, `notAnOperator`; `404` `unknownWorkItem`; `409` `pullRequestsNotRead`, `planChanged`, `alreadyApproved`; `422` `nothingTicked`, `stepNotOffered`, `stepNotGranted`, `stepNotSupported`, `mergeNeedsValidation`, `noPullRequests`; `423` `killSwitchEngaged`; `429` `tooManyRequests` (see `Approvals:ApprovalsLimit`); `503` `storeFull`, `auditUnavailable`. A draft pull request is never approved. The global stop answers `401` `notSignedIn` and `403` `approvalsDisabled`, `hostNotAllowed`, `originNotAllowed`, `notAnOperator`. `401` `notSignedIn` is a defensive answer of those two routes: with the sign-in on it is not reached, since a request without a session is stopped before. With the sign-in on, a request without a session is a `401` with no body, on every route of the API (unknown ones included), and a session without an identity the order can carry, or outside the reader group, is a `403`; a change that does not come from the application's own origin (an `Origin` that is not `SignIn:PublicUrl`, or `Sec-Fetch-Site` saying another site) is a `403` `originNotAllowed`.

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
| `Approvals:Mode`, `Approvals:DevelopmentUser` | `Disabled`, `dev-operator` | `Disabled`: nobody can approve. `Development`: a fixed sandbox operator with no sign-in; **the host will not start** with it outside the Development environment or unless it listens on the loopback, and the routes that record something answer only to a local request (`localhost` or a loopback address, from a client on the loopback). `OpenIdConnect`: people sign in (the next rows) |
| `Approvals:Mode=OpenIdConnect`, `SignIn:Authority`, `SignIn:ClientId`, `SignIn:ClientSecret`, `SignIn:PublicUrl` | none | People sign in through the identity provider (authorization code flow with PKCE, a confidential client). The authority and the public address are https (http only on the loopback of a Development machine), the secret goes through the environment or `dotnet user-secrets`, never a file; the host does not start without them. The public address is where the browser reaches Orchestrarr (an origin): the redirect addresses and the origin accepted for a change are built from it |
| `SignIn:OperatorGroup`, `SignIn:ReaderGroup`, `SignIn:GroupsClaim` | `orchestrarr-operators`, unset, `groups` | Members of the operator group (in that claim of the identity token) are operators; anybody else who signed in can read, not approve or stop. A group is compared **exactly as the claim writes it**, so a provider that sends the full path of a group (Keycloak with *full path* on) is configured with it (`/orchestrarr-operators`), and a group of the same name elsewhere in a tree does not count. With a reader group, only its members and the operators read; **without one, anybody who signed in reads: every account of the provider's realm** |
| `SignIn:StepUpAcr`, `SignIn:SessionHours`, `SignIn:KeysDirectory` | unset, 8, unset | The authentication context that counts as a second factor (read, not asked for yet); the length of a session in hours, 1 to 24 (not extended by use, and not revoked before: see ADR 0011); a folder for the keys that protect the session cookie, so that a new container or a second replica still reads it (**a secret**: the keys sit there unencrypted; without it the keys stay in the container and the host warns, outside Development) |
| `SignIn:Scopes`, `SignIn:ClaimsFromUserInfo` | `openid profile`, false | What is asked of the provider: a list here **replaces** the default (it must include `openid`). The groups must reach the session: in the identity token (a provider that only sends them for a scope of its own: add it to `Scopes`), or read at the user-information endpoint with `ClaimsFromUserInfo`, which adds the groups (`SignIn:GroupsClaim`, to those of the identity token) and `preferred_username`, plus what the handler maps by default (`sub`, `name`, `given_name`, `family_name`, `profile`, `email`, added unless the identity token carried the same value (when they differ, both are kept and the identity token's comes first)), and never the authentication context; no token is kept either way. A subject must be made of letters, digits and `. _ - @ : +`: anyone else signs in but cannot read |
| `Approvals:Mode=OpenIdConnect`, no grant | | To sign in and read without approving anything, leave `Powers:Grants` empty: nothing is granted, so nothing can be approved |
| `Storage:Provider`, `Storage:Path` | unset (see below), `data/orchestrarr.db` | Where the approvals, the audit trail and the global stop are kept. `Memory` forgets them at a restart (the sandbox); `Sqlite` keeps them in a file, created with its folder, and **an engaged global stop stays engaged** after a restart. A stop whose state cannot be read counts as engaged, and one that cannot be released for certain stays engaged. **Unset, it is `Memory` in Development or when nobody can approve, and the host refuses to start otherwise**, so that a restart never releases the stop by omission; the image sets `Sqlite`. **A database created outside Development starts with the stop engaged** (and a trace says so): release it once, deliberately. A file that cannot be used stops the host at its start. A relative path is read from the content root; the image sets `Sqlite` and `/config/orchestrarr.db` With the sign-in on and the store in memory outside Development, the host also logs a warning at its start |
| `Storage:MemoryStartsEngaged` | unset | With `Storage:Provider=Memory`: whether the stop starts engaged. Unset, it does outside Development when approving is on (a store that forgets everything is a new store, and a new store starts engaged); `false` says otherwise |
| `Approvals:ApprovalsLimit`, `Approvals:ApprovalsWindowMinutes` | 30, 1 | How many times a person (or, with nobody signed in, an address) may try to approve in a window: more is a `429`, before anything is recorded or traced, so that a loop of refusals cannot fill the trail. The global stop is never limited. The same person refused for the same reason is traced once every ten minutes |
| `Approvals:QueueCapacity`, `Approvals:AuditCapacity` | In memory 500 and 2000; in a file 10,000 and 200,000 | How many approvals and trail entries are kept before refusing (the trail needs at least 2). A full trail refuses new approvals first, and keeps a twentieth of itself (at least one entry) to trace the global stop and its release |
| `Fleet:SampleFile`, `Forge:SamplePullRequestsFile` | Development only | Sample data of `samples/`; a configured Portarr or token wins over them |

**At the identity provider**, register a confidential client with the authorization code flow and PKCE (S256), the redirect address `<SignIn:PublicUrl>/auth/callback` and the post-logout address `<SignIn:PublicUrl>/`, the scopes `openid profile`, and a mapper that puts the groups in the **identity token** (claim `SignIn:GroupsClaim`). `samples/keycloak` is a worked example.

`dotnet run` runs as Development: it enables the sandbox operator (`dev-operator`) and grants validate and merge on every repository, so the Approve button works. The image runs as Production, where approving is off unless you turn the sign-in on (`-e Approvals__Mode=OpenIdConnect -e SignIn__Authority=… -e SignIn__ClientId=… -e SignIn__ClientSecret=… -e SignIn__PublicUrl=https://…`, and keep the session keys in a volume with `SignIn__KeysDirectory`), and it does not carry `appsettings.Development.json`. The file is the state of the product: keep `/config` on a local disk (not a network share), owned by the image's user (`$APP_UID`: with a bind mount or a volume claim, set the owner or `fsGroup`, otherwise the host stops at its start), run a **single replica** (a rollout that starts the new pod before stopping the old one is a second writer: use a `Recreate` strategy), and back it up with SQLite's online backup from a container that has the `sqlite3` tool and mounts the volume (`sqlite3 /config/orchestrarr.db ".backup /config/copy.db"`; the image does not ship it, and a copy of the file alone while the host runs can be torn). It holds the identifiers of people and their decisions, no credential. **When the queue (10,000 approvals) or the trail (200,000 entries) is full**, every approval is refused (`storeFull`, `auditUnavailable`) until you archive and start again: stop the host, take the online backup, move the file away and start the host, which creates a new database that **starts with the stop engaged**; release it once, deliberately. The retention policy itself is Q-24. It listens on port 8080 as a non-root user: `docker run -p 8080:8080 -v orchestrarr-config:/config -e Portarr__BaseUrl=... -e Portarr__ApiKey=... -e GitHub__Token=...`.

> **Security.** With the default `Disabled` the API has **no authentication**: it reads and shows the state of your fleet to anybody who can reach it, and approving answers 403. Either turn the sign-in on (`Approvals:Mode=OpenIdConnect`: every route of the API then needs a session, approving needs the operator group, and the files of the interface stay public since they say nothing about the fleet), or put the image behind an authenticating front (single sign-on, a private network) and do not publish its port. The sandbox operator (`Development`) is refused outside Development and off the loopback. Never start the image with `ASPNETCORE_ENVIRONMENT=Development`.

## License

GPL-3.0 is intended, like the *arr apps; there is no `LICENSE` file yet and the spec does not settle it.
