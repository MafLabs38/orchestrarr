# Orchestrarr — Functional and Technical Specification

> **Status**: draft v0.8. Built: intake and decision cards, the dependency pull requests from the forge, the control-plane skeleton in Core, the approval of a card (queue, audit trail and global stop, in memory), and the sign-in through the identity provider. **Nothing runs**: no launcher takes the approvals.
> **Related documents**: [VISION.md](VISION.md) (pitch, safety principles, first interview questions) · [environment-file.md](environment-file.md) (standalone description format) · [adr/](adr/) (architecture decisions)
> **Last updated**: 2026-11-03
> **Audience**: the project owner and contributors.
>
> ⚠️ This document is intended for the **public repo**. It contains **no real infrastructure values**. All names (`server-1`, `homelab.example`, `app-a`…) are examples.

## Legend

| Marker | Meaning |
|---|---|
| ✅ | **Frozen**: decided by the maintainer, does not change without a new ADR |
| 🟡 | **Proposed**: drafted by the analyst, pending validation |
| ❓ | **Open**: unanswered question, see §9 |

Identifiers: `D-xx` decision · `F-XXX-nn` feature · `UC-nn` use case · `Q-xx` question.

---

## 1. Vision

Orchestrarr takes the **maintenance work** off the operator: it turns what [Portarr](https://github.com/MafLabs38/portarr) sees (vulnerable dependencies, open dependency-bot pull requests, outdated images, broken applications) into **work items** that an agent carries out, from the fix to the deployment and the check that nothing broke.

### 1.1 The problem

The operator runs a growing number of applications. Portarr shows what is wrong; dependency bots open pull requests; **nobody validates them**. They pile up, technical debt grows, and the longer it waits the harder it gets to catch up.

In practice the operator rarely reads code: they check that the AI did not drift, and that is visible quickly. The work to remove is the *execution* (check out, bump, test, merge, deploy, verify), not the *decision*.

### 1.2 The idea in one sentence

**Orchestrarr never decides, but it executes a decision end to end.** The operator gets a short **decision card** (a few pre-ticked checkboxes), confirms in one or two clicks, and an agent does everything the card allows, then reports.

### 1.3 Out of scope

- Detecting problems and describing the fleet: **Portarr**.
- Remembering knowledge long term: **Mnemarr**.
- Being a CI system, a general workflow engine or a deployment platform: Orchestrarr drives the operator's existing CI and deployment means, it does not replace them.

---

## 2. Decisions

| # | Decision |
|---|---|
| ✅ D-01 | **No autonomy of decision.** Orchestrarr never starts a work item on its own. Every work item starts from an explicit operator approval. |
| ✅ D-02 | **Autonomy of execution, once approved.** After approval an agent works alone, in isolation, up to the limit of what the operator ticked. |
| ✅ D-03 | **Portarr detects and notifies; Orchestrarr acts.** Findings come from Portarr's API / MCP. Notifications (email first) are sent by Portarr, with a link to Orchestrarr. Orchestrarr does not scan the fleet. |
| ✅ D-04 | **The decision card.** For each work item, a plan made of steps (for example: validate, merge, deploy to environment X), **ticked by default**; the operator unticks what they do not want and confirms. One or two clicks in the common case. |
| ✅ D-05 | **v1 scope: routine maintenance.** Validating and merging dependency-bot pull requests (the existing backlog first), fixing findings, and redeploying where the application is installed. |
| ✅ D-06 | **Where to deploy comes from Portarr.** The environment where an application is installed determines the target. Orchestrarr does not keep its own inventory. |
| ✅ D-07 | **"No regression" means: redeploy, then smoke tests.** Not end-to-end tests. At minimum, the application starts and answers. |
| ✅ D-08 | **Rollback.** The previous version is kept; if the deployment or its smoke tests fail, the previous version is restored and the operator is told what happened. |
| ✅ D-09 | **Direct to the target environment, no pre-production.** Accepted by the maintainer *because* rollback keeps the downtime minimal (D-08). |
| 🟡 D-10 | **Powers are granted per application and per environment, and are off until the operator enables them.** An application is eligible for automatic deployment only if the environment description lets Orchestrarr know *how it is deployed*, *how to check it* and *how to roll it back* (D-17). Otherwise the card offers fewer steps (for example merge only). |
| 🟡 D-11 | **Three independent power levels**: (1) analyse and test, read-only; (2) fix and merge; (3) deploy, per environment. Each is granted separately; a step missing from the grant is not offered on the card. |
| 🟡 D-12 | **Email links never act by themselves.** Mail scanners and previews open links on their own. A link opens the decision card (signed, expiring, single use); the click that starts the work is a button on that page. |
| 🟡 D-13 | **Desktop clients with auto-update are a special case.** Deploying means publishing a release; once clients have updated, it cannot be taken back. All checks happen *before* the release, and the release is its own step on the card. |
| ✅ D-14 | **The safety principles of [VISION.md §5](VISION.md) stay**, as amended by [ADR 0001](adr/0001-merge-and-deploy-on-operator-approval.md) (D-15). |
| ✅ D-15 | **Orchestrarr merges and deploys itself** on the operator's tick; the tick is the human approval. Autonomy of execution is the point of the product. [ADR 0001](adr/0001-merge-and-deploy-on-operator-approval.md). |
| ✅ D-16 | **Everything is ticked by default on the card, deployment included.** The operator unticks what they do not want. |
| ✅ D-17 | **Applications declare nothing to Orchestrarr.** How and where an application is deployed is **observed** through the environment description (Portarr). [ADR 0002](adr/0002-environment-description-is-a-provider.md). |
| ✅ D-18 | **Orchestrarr is a satellite of Portarr, and the environment description is a provider.** Portarr is the reference one. For standalone use, a second provider reads a **structure file** (YAML, infrastructure-as-code style) describing the same things. Both fill the same internal model. If Portarr lacks something, Portarr is fixed, not duplicated. |
| ✅ D-19 | **The review is not the safety net, the operator's tick is.** Orchestrarr's identity may approve and merge its own pull requests, as the maintainer's other agents already do. GitHub accounts, tokens and rulesets are set up to give Orchestrarr exactly the rights it needs, no more (D-10). |
| 🟡 D-20 | **The environment file is a subset of Portarr's catalog plus a `deployment` key** ([environment-file.md](environment-file.md)). Portarr's catalog has the code `repository:` but no per-application deployment source yet: the key is added to Portarr as well (ADR 0002). |
| 🟡 D-21 | **The unit of work is the repository, or the image for a third-party image; not the application.** Work item ids are `repository:<owner>/<name>` and `image:<reference>`, frozen before anything persists against them. A fix, a merge and a build happen at a repository; an application can span several repositories and a repository several applications, so Portarr's `appIds` stay an attribute. Forks and archived repositories are not work items (Q-10 will make this an allow-list). [ADR 0003](adr/0003-work-items-are-per-repository.md). |
| 🟡 D-22 | **The server sends data and codes; the interface words them.** Findings, plan steps, reasons and source errors are codes and numbers: no sentence travels in the API, because the contract outlives one client. No exception text reaches a client: a failed read gives a code (`unreachable`, `unauthorized`, `timeout`, `invalidResponse`, `rateLimited`, `failed`) and the details stay in the log. |
| 🟡 D-23 | **"Available" and "granted" are two things.** `available`: possible, from what the source says. `granted`: allowed by the powers granted to Orchestrarr (D-10, D-11). Powers come from the configuration (`Powers:Grants`, D-53): none until the operator lists one, so by default no step is granted and a card is a preview. A scan that is off or unreadable is a finding, not a clean repository. |
| ✅ D-24 | **The worker is Claude Code, run non-interactively, in a container started for the run.** [ADR 0004](adr/0004-repair-runs-in-disposable-workspaces-on-a-workshop.md). |
| ✅ D-25 | **A workshop is a machine where workers run and build, separate from the target environments.** It has a root directory, a limit of concurrent runs, resource limits (settings, default three of four cores, adjusted after measuring) and a retention time; the first one is the development host, local. A repair runs in a disposable workspace, one directory per run with a codified name, from a fresh checkout. |
| ✅ D-26 | **The worker holds a repository credential only** (a GitHub App installation token scoped to one repository, minted for the run). **It never holds deploy credentials**: merging and deploying are done by Orchestrarr's own code, with credentials scoped to one target and one ticked step. |
| ✅ D-27 | **The model credential is the maintainer's subscription token, not an API key.** Budgets are in attempts, turns and minutes (defaults: 3 attempts, 20 minutes, one run at a time). A run that hits the quota is paused and retried, not failed. |
| 🟡 D-28 | **The worker's network is restricted** to the model provider, the forge and the package registries, because the subscription token is a long-lived secret an injected prompt could try to send out. |
| ✅ D-29 | **Every analysis and every fix uses Claude Sonnet.** The model and the amount of reasoning are settings, not code. |
| ✅ D-30 | **Every repair comes with a regression report** (F-RUN-04): the maintainer asked to see what the tests proved, not only that they passed. |
| 🟡 D-31 | **Acting on a machine is a power like the others** (D-10, D-11): configured per machine (an SSH connection: user, key by reference, allowed actions), off until the operator enables it, never beyond what the card ticked. The connection credentials are held by Orchestrarr's own code, **never by a worker** (D-26). |
| 🟡 D-32 | **Prefer changing the declared state to running a command.** A fix to the infrastructure is a pull request on the repository that declares it (the infrastructure-as-code repositories), applied by Orchestrarr's deployer. A command on a machine is the exception, approved as its own step, with the output recorded. A diagnosis is read-only first. |
| 🟡 D-33 | **On an environment, the agent acts as Claude Code would**: it reads the repository (its documentation, its instructions, its pipeline), understands how that project is operated (infrastructure as code with its own tool, a container, an application on bare metal) and does what the project itself says. No rule per type of project is written into Orchestrarr. **No initiative**: when it doubts, or when the project documents no way to do it, it stops and reports. |
| 🟡 D-34 | **No container where the agent must act on the infrastructure** (ADR 0004, revision). The safety is: a dedicated operator account per environment with only the rights its projects need; a plan written before acting and recorded; an **explicit approval** for what cannot be undone (destroying, deleting, removing a volume, forcing, applying a plan that destroys); budgets and the kill switch. The disposable workspace stays for repairing code (D-25). |
| 🟡 D-35 | **A journal per environment**: a work folder in each environment where the agent writes what it planned, ran and found, readable on the spot, and **mirrored as it goes** to Orchestrarr's own audit store, which is the reference (an agent with rights on a machine could alter a local file). |
| 🟡 D-36 | **Portarr names the repositories with waiting dependency pull requests; Orchestrarr reads their live state from the forge it will act on**, through a provider (GitHub first) with a read-only credential. A forge failure is said on the work item it concerns, never as an empty list. [ADR 0005](adr/0005-pull-requests-are-read-from-the-forge.md). |
| 🟡 D-37 | **An approval binds to (repository, number, head commit).** The contract carries the head commit, so that a bot that rewrites its branch after the tick is not merged unseen. |
| 🟡 D-38 | **"Ready to merge" is decided once, in Core** (not a draft, every check passes, the forge says clean) and sent as data. |
| 🟡 D-39 | **The security of the agent's power is in who can cause a launch, not in limiting what the launched agent can do.** On its environment the agent is omnipotent, on production virtual machines, never on the maintainer's own machine. [ADR 0006](adr/0006-a-launch-is-a-signed-order-and-credentials-are-short-lived.md). |
| 🟡 D-40 | **Who orders is authenticated by an OpenID Connect identity provider** (Keycloak in the reference setup): approving needs the operator role; approving a work item that touches production needs a **step-up** (a fresh second factor). |
| 🟡 D-41 | **A launch is a signed, single-use, time-limited order built by the server** from the plan the operator ticked, checked and executed by a **launcher** that is the only thing able to start an agent. No endpoint accepts a free command or a free prompt; the agent cannot start a run. |
| 🟡 D-42 | **No standing credential.** The dedicated Vault (D-44) issues credentials for one run and one environment at launch (secure shell certificate, cluster token, GitHub App token, the model credential); the machines accept only certificates of that authority for the operator principal. |
| 🟡 D-43 | **The product fails closed and has a kill switch**: no audit sink, no order; the kill switch stops the launcher and the signing and revokes the active leases. |
| 🟡 D-44 | **A dedicated Vault, separate from every other Vault, tied to the identity provider.** It is deployed by its own infrastructure-as-code repository outside Orchestrarr (platform layer, configuration layer); people sign in through the identity provider's OpenID Connect, groups map to policies. [ADR 0007](adr/0007-a-dedicated-vault-and-a-small-control-plane.md). |
| 🟡 D-45 | **One identity per component, with the right to sign split from the right to obey**: the control plane can only sign an order (non-exportable key), the launcher can only verify it and request the credentials it names, the agent run authenticates to nothing. No single compromised component can both sign and execute. |
| 🟡 D-46 | **The control plane is small and only launches.** The API decides and signs; the **launcher** has no inbound port and **pulls** signed orders from the store; the agent run acts. An attacker cannot call the launcher, and only a signed, unexpired, unused order within the granted powers makes it do anything. |
| 🟡 D-47 | **Whoever gives an order is authorized twice and is always a person**: the API checks the session opened with the identity provider (the operator group, step-up on production) before signing, the launcher checks the signature and the powers again. A service client cannot hold the operator role. |
| 🟡 D-48 | **The dedicated Vault runs on the production cluster, like every production element**, deployed by the cluster's GitOps repository and configured by its own infrastructure repository. It starts sealed and is unsealed deliberately; the credential issued to a run for the cluster excludes the Vault's namespace. [ADR 0007](adr/0007-a-dedicated-vault-and-a-small-control-plane.md). |
| 🟡 D-49 | **A fifth review agent, the `spec-reviewer`, keeps the documentation true**: it compares the README (the entry point), the docs, the spec and the ADRs with the implemented state, in both directions, gives the exact text to write, and runs with the other four on every pull request. [ADR 0008](adr/0008-a-fifth-agent-keeps-the-documentation-true.md). |
| 🟡 D-50 | **Releases are decided by semantic-release on `main`, which takes merge commits only**: the version comes from the Conventional Commits, the image is tagged with it, and the cluster's GitOps deploys the newest version tag. A squash of the release pull request would hide the commits, so the repository forbids it. [ADR 0009](adr/0009-releases-are-decided-by-semantic-release-from-main.md). |
| 🟡 D-51 | **The parameters of an order, as the skeleton enforces them** (all configuration): an order lives 5 minutes, the launcher refuses more than 10 whatever the order says, a clock tolerance of 30 seconds applies to the issue time only, a second factor is fresh for 5 minutes. The server decides what production is from the environment description (an unknown environment is production) and signs the second factor's time into the order, so the launcher checks it again. An order is for one launcher (its audience). The signed form is length-prefixed (ADR 0006, canonical form v2). Missing, unreachable or unknown always means refused. |
| 🟡 D-52 | **Approving records a decision, not an order**: this pull request at this commit, these steps, this approver. The client sends the work item, the steps it ticked and **the pull requests and commits it displayed**; the server decides everything else and refuses (`planChanged`) unless what was displayed is exactly what is there now, so an approval never covers a commit that arrived after the operator looked. A draft pull request is never approved. A step is approved only if it is offered, granted, recorded by the vertical and, for a merge, validated first. The order is issued at run start. [ADR 0010](adr/0010-approving-records-a-decision-and-the-sandbox-operator.md). |
| 🟡 D-53 | **Powers are granted in the configuration** (`Powers:Grants`), off until listed, checked when the host starts, and a grant on every repository may only validate outside the sandbox. **There are two approvers: a person signed in through the identity provider (D-54), and, for the local sandbox only, a sandbox operator.** The host refuses the sandbox operator outside the Development environment and unless it listens on the loopback; in that mode the two routes that record something answer only to a local request (a `Host` of `localhost` or a loopback address, from a client that is itself on the loopback: from another machine, through a tunnel that ends there), and the image does not carry the sandbox's settings. In-memory, bounded stores: a full queue or trail refuses, it never forgets; refusals have a smaller trail budget than decisions. [ADR 0010](adr/0010-approving-records-a-decision-and-the-sandbox-operator.md). |
| 🟡 D-54 | **People sign in through the identity provider, in the host**: the authorization code flow with PKCE as a confidential client, a session cookie and no token in the browser; everything the API says is for a person who signed in; an operator is a member of a configured group; the addresses come from the configuration; a change must come from the application's own origin ([ADR 0011](adr/0011-people-sign-in-through-the-identity-provider.md)). Refines D-40. |

---

## 3. Use cases

| # | Use case |
|---|---|
| UC-01 | I get an email from Portarr: "3 findings need a decision". One click opens the decision card in Orchestrarr. |
| UC-02 | On the card, each finding shows what changes, what was tested, the risk, and the planned steps, all ticked. I confirm, or untick *deploy* and only merge. |
| UC-03 | I have a backlog of dependency-bot pull requests. Orchestrarr groups them per repository (D-21), validates each one (build, tests), and proposes them on cards instead of leaving them to rot. |
| UC-04 | After a deployment, a smoke test fails. The previous version is restored automatically, and I see what happened and why. |
| UC-05 | A desktop client with auto-update needs a new release. Orchestrarr builds and checks it, and publishes only when I ticked the release step. |
| UC-06 | I stop everything at once with a global stop. |
| UC-07 | Three of my backups do not work. I see which ones, why, and I approve a repair that Orchestrarr carries out on the machine, then checks that a backup now succeeds. |
| UC-08 | A fix has to be proved where the application runs: Orchestrarr tests it on the target environment, and tells me what it did and what it found. |
| UC-09 | After a repair I read a **regression report**: which tests ran before and after, what changed, whether the finding is gone. |

---

## 4. Domain model

| Entity | Meaning |
|---|---|
| **Finding** | A problem seen by Portarr (vulnerability, open dependency pull request, outdated image, failing application). Read-only for Orchestrarr. |
| **Work item** | The findings of one repository (or one third-party image, D-21), grouped, to carry out. Has a lifecycle (below). |
| **Plan** | The ordered steps of a work item (validate, merge, deploy to environment X, verify). Each step is ticked or not on the card. |
| **Decision card** | The plan as shown to the operator, with the verdict (what changes, tests run, risk). |
| **Run** | One attempt of an agent to execute a plan, with its budget, logs and evidence. |
| **Power grant** | What Orchestrarr may do for one application in one environment: the granted levels (D-10, D-11). The deploy method is observed, not declared (D-17). |
| **Verdict** | The summary that lets the operator decide without reading the code: diff summary, build and test results, regression check, risk. |

Work item lifecycle: `Proposed` → `AwaitingDecision` → `Approved` → `Running` → `Done` | `RolledBack` | `Failed` | `Rejected` | `Cancelled`.

---

## 5. Architecture 🟡

One container, API first; the UI consumes the REST API (*arr pattern). Pluggable providers:

| Provider type | Role | v1 candidate |
|---|---|---|
| Findings source | Gives findings (D-03) | Portarr (API / MCP) |
| Environment description | Gives environments, runtimes, workloads, applications and the sources of their code and deployment (D-17, D-18) | Portarr (reference); a YAML structure file for standalone use |
| Forge | Branches, pull requests, merges, CI status | GitHub |
| Agent runtime | Runs the worker agent | ❓ Q-07 |
| Deployer | Redeploys and rolls back one application, chosen from what the environment description shows (runtime kind, deployment source) | ❓ Q-03 |
| Verifier | Smoke test after deployment | ❓ Q-06 (Portarr health checks are the candidate) |
| Memory | Runbooks read before acting, outcomes written back | Mnemarr |
| Launch (Core) | The **issuer** (control-plane side: authorizes, builds, signs) and the **launcher** (verifies, cannot sign: it has no signer), joined only by the signed order. Ports: signer, verifier (asymmetric), nonce ledger, kill switch, audit sink, granted powers, environment classifier. Faked in tests, except the kill switch, the audit sink and the granted powers, which have in-memory implementations used by the approval vertical (D-53); no signer, verifier or ledger yet | ❓ Q-23 for the Vault adapter |
| Approvals (Core, host) | The approval store, the audit trail and the global stop, behind `IApprovalStore`, `IAuditLog` and `IKillSwitchControl`; the routes depend on the interfaces | in memory, bounded, refuse when full (D-53); a persistent store comes with the workshop |
| Sign-in (host) | People sign in through an OpenID Connect identity provider; the host keeps the session | any provider; a local Keycloak in `samples/keycloak` (D-54) |
| Notifier | Tells the operator | Portarr sends (D-03); Orchestrarr shows status in its UI |

Today one interface (`IFleetSource`) gives both the findings and the environment, because Portarr's maintenance report carries both. It will be split into a findings source and an environment description when the structure-file provider (F-INT-03) needs it; the host, not a provider, chooses which source is used.

Screens in *arr style: **Queue** (running and waiting), **Decisions** (cards awaiting approval), **History**, **Activity** (audit trail), **Settings** (applications and powers, budgets, kill switch).

---

## 6. Features (draft)

| # | Feature |
|---|---|
| F-INT-01 | Read findings and the environment of each application from Portarr |
| F-INT-02 | Group findings into work items, rank by severity |
| F-INT-04 | Read the open dependency pull requests of each repository from the forge (GitHub first): what each updates and how far, whether its checks pass and whether it can be merged. Read-only. A forge that is not connected or fails is said, never shown as an empty list |
| F-INT-03 | Environment-description provider reading a YAML structure file (standalone mode, D-18) |
| F-CRD-01 | Decision card with ticked steps and a verdict. Built: the ticked steps are approved (recorded per pull request and commit); the verdict half comes later |
| F-CRD-02 | Signed, expiring, single-use link to a card (D-12) |
| F-RUN-01 | Run an agent in an isolated clone with a narrow credential and a budget |
| F-RUN-02 | Validate a dependency pull request: build, tests, regression check |
| F-RUN-03 | Merge when ticked, deploy per environment when ticked, smoke test, rollback |
| F-RUN-04 | **Regression report** of every repair: the tests before and after (counts, names of those that changed state, durations), the lint and the scan results, the finding that was targeted (gone or not), the files changed and the coverage of the change. It is part of the verdict of the decision card and of the pull request (D-30) |
| F-ENV-01 | **Machines Orchestrarr may act on**, each with its own connection (SSH first): user, key by reference, the actions allowed. Off until the operator enables them (D-31) |
| F-ENV-02 | **Diagnose on a machine**: read-only commands, to find why something fails (a backup, a service) |
| F-ENV-03 | **Act on a machine**: a declared action, approved as a step of the card, with the output kept in the audit trail and a check that it worked (D-32) |
| F-BKP-01 | **Backups that fail or are missing are findings** (read from Portarr, which already follows the backups) and become work items like any other (UC-07) |
| F-SAF-01 | Global stop, dry-run mode, budgets (cost, time, concurrency). Built: the global stop; the rest comes with the launcher |
| F-SAF-02 | Full audit trail of prompts, tool calls, decisions and approvals. Built: approvals, refusals and the global stop, in memory (Activity screen); the rest and a persistent store come with the workshop |
| F-APP-01 | Powers per application and environment, off until enabled (D-10). Built for the steps of a repository (`Powers:Grants`, D-53); per environment comes with deploy |
| F-MEM-01 | Read runbooks from Mnemarr before acting, write outcomes back |

---

## 7. Safety 🟡

The principles of [VISION.md §5](VISION.md) apply, as amended by [ADR 0001](adr/0001-merge-and-deploy-on-operator-approval.md): Orchestrarr **merges and deploys on the operator's tick** (D-15). The tick *is* the human approval. In exchange, Orchestrarr holds credentials that can merge and deploy, which makes the following non-negotiable:

- Powers per application and per environment, off until enabled (D-10, D-11).
- A merge or deploy credential exists only for the duration of one approved step, scoped to one repository or one target.
- Branch rulesets on the forge stay the last line of defence.
- Rollback is a precondition of automatic deployment (D-08, D-10).
- Findings, changelogs and repository content are untrusted data, never instructions.
- **Orders fail closed** (D-43, D-51). The issuer refuses (`IssueRefusal`): kill switch, not a person, not an operator, invalid request, production without a fresh second factor, signer unavailable, audit unavailable. The launcher refuses (`LaunchRefusal`), in this order: kill switch, verifier unavailable, bad signature, malformed fields, wrong audience, not yet valid, expired, lifetime too long, production without the second factor, outside the granted powers, replayed; the kill switch is read again before accepting. The order's id is recorded last, so an order refused earlier can be retried, and it stays spent when the audit then fails. Nothing is signed before its trace (`order.signing`), and nothing an unverified order claims reaches the trail.
- **Approvals fail closed** (D-52, D-53): no approval without its trace, none on a list of pull requests that is not read, changed since it was displayed, that repeats one or that only has drafts, none for a step that is not offered, granted or recorded, none for a merge without validation, none while the global stop is engaged; every refusal is traced with its code, the stop is applied even when its own trace cannot be written, and the trail keeps room for decisions and stops when refusals pile up.
- **The API is for people who signed in** (D-54): with `Approvals:Mode=OpenIdConnect` every route of the API needs a session (401 without one, never a redirect; the files of the interface stay public), only members of the operator group approve or stop, and a change must come from the application's own origin (`originNotAllowed`). The host does not start without an https authority and public address (http only on the loopback in Development).

---

## 8. Roadmap 🟡

| # | Vertical | Status |
|---|---|---|
| 1 | **Intake from Portarr and read-only decision cards** (F-INT-01, F-INT-02, F-CRD-01), before any write power exists | built, reviewed in the sandbox by the four review agents. The *verdict* half of F-CRD-01 and the eligibility of *deploy* (a known check and rollback, D-10) come with later verticals, so deploy is shown as a preview. Reading Portarr every 30 seconds is the interim answer to VISION question 8 (poll or webhook) |
| 2 | **Read the dependency pull requests and their checks from the forge** (F-INT-04); Portarr only gives their number | built, reviewed. Read-only, GitHub; the next vertical validates and merges them |
| 2b | **Control-plane skeleton**: signed launch orders, the issuer that authorizes whoever orders, the launcher checks, refusal codes (D-41, D-45 to D-47, D-51; ADR 0006, 0007) | built in Core, reviewed by the five agents, **the ports of the kill switch, the audit sink and the granted powers are wired in the host by the approval vertical (row 2c, in memory)**; the issuer, the launcher, the signer, the nonce ledger and the environment classifier are not: no store the launcher pulls from, no Vault adapter, no lease, concurrency or budget check, no order endpoint. It starts nothing and grants no power |
| 2c | **Approve a decision card**: the tick becomes a recorded approval per pull request and commit (F-CRD-01), with the queue, the audit trail (F-SAF-02) and the global stop (F-SAF-01), in memory (D-52, D-53; ADR 0010) | built, reviewed by the five agents. No launcher takes the approvals yet: nothing runs. Before production: a persistent trail and a stop that starts engaged (the sign-in is row 2d) |
| 2d | **Sign in through the identity provider** (D-40, D-54, Q-12; ADR 0011): the session cookie, the operator group, the reads of the API behind it, the origin check, the interface that signs in by itself | built, with a local Keycloak for the demonstration (`samples/keycloak`). The approvals and the trail are still in memory; the step-up is read, not asked for; a session is not revoked before it ends (ADR 0011) |
| 3 | Validate and merge: next, with the launcher and the workshop (D-24, D-25; ADR 0004, 0007, 0010); uses 2b, 2c and 2d | not started |
| 4 | Deploy, smoke test and roll back (F-RUN-03), with the regression report (F-RUN-04) | after 3 |
| 5 | Backups and actions on machines: find the backups that fail (F-BKP-01), diagnose and act through a configured connection (F-ENV-01 to F-ENV-03) | **not started**: the maintainer wants the repair vertical to be correct first. Needs Q-16 and Q-17 |

The acceptance scenarios are the rotten test repository's `SCENARIOS.md`.

User interface, deferred: the light theme (its accent text color is ready, nothing selects it yet) and the color-impaired mode that the shared design makes mandatory.

---

## 9. Question register

| # | Question |
|---|---|
| ✅ Q-01 | **Amend the safety model.** Resolved: Orchestrarr merges and deploys on the tick (D-15, ADR 0001). |
| ✅ Q-02 | **Defaults on the card.** Resolved: everything ticked, deployment included (D-16). |
| ❓ Q-03 | **What must the environment description provide?** Applications declare nothing (D-17). Portarr's catalog gives environments, runtimes, workloads, the link app ↔ workload, a smoke test (`check`) and the code `repository:`. **Missing: the deployment source** per application (D-20). Remaining to check against Portarr's API / MCP: what is actually exposed, and whether a runtime's previous revision or image is reachable for rollback. |
| ❓ Q-04 | **Rollback and data.** Restoring the previous version does not undo a database migration. Which applications are stateful, and are they eligible for automatic deployment? |
| ❓ Q-05 | **Desktop clients (D-13).** Is a "release" step ever pre-ticked? Is there a staged rollout or a beta channel? |
| ❓ Q-06 | **Smoke tests.** Are Portarr's health checks enough (the application answers for N minutes), or does each application declare its own check? |
| ✅ Q-07 | **Agent runtime.** Resolved: Claude Code, non-interactive, in a container (D-24, ADR 0004). |
| ✅ Q-08 | **Where workers run, and how builds are kept from saturating a small host.** Resolved: on a workshop host, in containers capped by the host's resource-limited slice, one run at a time (D-25, ADR 0004). |
| 🟡 Q-09 | **Budgets.** Proposed: in attempts, turns and minutes, not in money, because the model credential is the maintainer's subscription (D-27). The exact ceilings per day and per month are open. |
| ❓ Q-10 | **Allow-list of repositories**, and whether infrastructure-as-code and container definitions are in scope. |
| ❓ Q-11 | **The existing backlog of dependency pull requests.** Order, conflicts between pull requests of one application, grouping. |
| ✅ Q-12 | **Authentication** for the UI and the API. Resolved for people: D-54, in the host, with the identity provider. Left open and not needed yet: API keys for machines (a service account cannot be an operator, D-47), and the link in a mail, which opens the page and signs in first. |
| ❓ Q-13 | **Standalone mode**: manual work items when no Portarr is connected (VISION.md §3). |
| ✅ Q-14 | **Merging under required-review rulesets.** Resolved: Orchestrarr's identity may approve its own pull requests; accounts, tokens and rulesets are set up accordingly (D-19). |
| 🟡 Q-15 | **Structure file format** for standalone mode. Proposed: a subset of Portarr's catalog plus a `deployment` key ([environment-file.md](environment-file.md), D-20). To confirm when the first vertical reads it. |
| ❓ Q-16 | **How does Orchestrarr act on a machine?** A connection per machine (SSH, like Ansible), configured, never in a worker. Open: where the key lives, whether commands are free or only declared actions (playbooks run by Orchestrarr's own code), and whether an agent in a container may run commands through that connection (Claude Code in a container with its own narrow credential), or only propose them. Recommendation: diagnosis free but read-only, changes through the declared state (D-32), a free command only per machine and per approval. The maintainer's direction of 2026-11-03: the agent acts as Claude Code would, without a container, with no initiative (D-33 to D-35). |
| ❓ Q-17 | **What does a backup finding look like?** Which backup tools and which signals (last success too old, never ran, failed run). Portarr already reads the backups of a Backrest instance and reports unprotected data; the question is how much of that is enough to name a cause, and what a repair of each looks like (a configuration, a permission, a full disk, a credential). |
| ❓ Q-18 | **What is a "dependency pull request"?** Portarr counts Dependabot's; Orchestrarr reads the bots of its configuration (Dependabot and Renovate). Should Portarr count every bot, or expose the list (D-18)? Until then the two numbers can differ and the screens say so (ADR 0005). |
| ❓ Q-19 | **Where does the agent process run when it acts on an environment?** On the environment itself (the model credential would have to be there, on every machine), or on the workshop, which opens a session on the environment (the long-lived subscription token stays on one machine and is handed to the run only). Recommendation: the second, with the journal on the environment. |
| ✅ Q-20 | **Vault or a lighter authority?** Resolved: a new Vault dedicated to this purpose, tied to the identity provider, provisioned by its own repository (D-44, ADR 0007). Its issued SSH certificates replace any SSH authority of its own. |
| ❓ Q-21 | **Who approves production?** One operator with a step-up, or two people for what cannot be undone. |
| ✅ Q-22 | **Where does the dedicated Vault run, and how is it unsealed?** Resolved: on the production cluster (D-48), integrated storage, one node, unsealed by hand with key shares the maintainer holds; the unseal never becomes automatic.
| ❓ Q-23 | **How do clients reach the Vault, and over what?** Today: a plain HTTP listener inside the cluster, reachable only by namespaces that opt in, and a port-forward for people. Open: TLS (a certificate authority in the cluster, or the ingress), and a way in for the launcher when it runs outside the cluster.

---

## 10. Glossary

| Term | Meaning |
|---|---|
| Work item | A "chantier": one unit of maintenance work on one repository, or on one third-party image (D-21). |
| Decision card | The few-checkbox page where the operator approves a work item. |
| Verdict | The summary of what changed and how it was checked. |
| Smoke test | The minimal check that a redeployed application starts and answers. |

---

## 11. Changelog

| Date | Change |
|---|---|
| 2026-11-01 | v0.1: first interview. Decisions D-01 to D-09 and D-14 frozen; D-10 to D-13 proposed. |
| 2026-11-03 | v0.8: people sign in through the identity provider, in the host (D-54, Q-12 resolved for people; ADR 0011). |
| 2026-11-03 | v0.7 (amended): approving a decision card records an approval per pull request and commit, with the queue, the trail and the global stop; powers granted in the configuration (D-52, D-53; ADR 0010; roadmap row 2c). |
| 2026-11-03 | v0.7 (amended): the control-plane skeleton in Core, reviewed by the five agents: order, issuer, launcher, fail-closed rules, canonical form v2 (D-51; roadmap row 2b). |
| 2026-11-03 | v0.7 (amended): releases by semantic-release from `main`, merge commits only, deployed by GitOps (D-50, ADR 0009). |
| 2026-11-03 | v0.7: the second vertical: the pull requests read from the forge (D-36 to D-38, ADR 0005), the screens as a list of applications, Q-18 (what a dependency pull request is). |
| 2026-11-03 | v0.7 (amended): a fifth review agent keeps the documentation true (D-49, ADR 0008); the first run of it corrected the README, D-22 and UC-03. |
| 2026-11-03 | v0.6 (amended): the dedicated Vault runs on the production cluster (D-48, Q-22 resolved, Q-23 opened; ADR 0007). |
| 2026-11-03 | v0.6 (amended): a new Vault dedicated to this purpose and tied to the identity provider, the small control plane that only launches, the authorization of whoever gives an order (D-44 to D-47, Q-20 resolved, Q-22; ADR 0007). |
| 2026-11-03 | v0.6 (amended): the structure of security the maintainer asked for: who orders is authenticated (Keycloak), a launch is a signed order checked by a launcher, credentials are short-lived (D-39 to D-43, Q-20, Q-21; ADR 0006). |
| 2026-11-03 | v0.6 (amended): the maintainer's direction on acting on environments: the agent acts as Claude Code would, no container there, no initiative, a journal per environment (D-33 to D-35, Q-19; ADR 0004 revision). |
| 2026-11-03 | v0.6: the new scopes the maintainer named: the regression report of every repair (F-RUN-04, D-30), acting on machines through a configured SSH connection (F-ENV-01 to F-ENV-03, D-31, D-32), backups that fail as findings (F-BKP-01); use cases UC-07 to UC-09; roadmap row 5 (not started); Q-16 and Q-17. |
| 2026-11-03 | v0.5: the repair protocol: Claude Code in a container (D-24), the workshop host and the disposable workspaces (D-25), the worker never deploys (D-26), the subscription credential and the budgets in attempts and minutes (D-27), the restricted network (D-28), Sonnet for every run (D-29); Q-07 and Q-08 resolved, Q-09 proposed (ADR 0004). |
| 2026-11-03 | v0.4: the first vertical is built; its review made three decisions explicit (D-21 the unit of work, D-22 data and codes not sentences, D-23 available versus granted); Q-12 says the API has no authentication yet; roadmap and interface notes. |
| 2026-11-01 | v0.3: Orchestrarr may approve its own pull requests (D-19, Q-14 resolved); environment file format proposed ([environment-file.md](environment-file.md), D-20, Q-15); Q-03 narrowed to the missing deployment source. |
| 2026-11-01 | v0.2: merge and deploy on tick (D-15, ADR 0001), everything ticked by default (D-16), deployment observed not declared, Portarr as environment provider with a standalone structure file (D-17, D-18, ADR 0002). Q-01, Q-02 resolved; Q-03 reformulated; Q-14, Q-15 added. |
