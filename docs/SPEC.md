# Orchestrarr — Functional and Technical Specification

> **Status**: draft v0.12. Built: intake and decision cards, the dependency pull requests from the forge, the control-plane skeleton in Core, the approval of a card (queue, audit trail and global stop, kept in a SQLite file or in memory), and the sign-in through the identity provider. **Nothing runs**: no launcher takes the approvals. Decided, not built: the launcher is a Rust binary held to the control plane by shared conformance vectors (D-59), it authenticates to the control plane with a signed token of its own (D-62), and the launcher of the home environment also serves the orders that have no environment (D-63).
> **Related documents**: [security-model.md](security-model.md) (how an order is secured, with diagrams) · [VISION.md](VISION.md) (pitch, safety principles, first interview questions) · [environment-file.md](environment-file.md) (standalone description format) · [adr/](adr/) (architecture decisions)
> **Last updated**: 2026-11-04
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

> **Amendments to decisions.** *Decided* (D-63, [ADR 0016](adr/0016-the-launcher-acts-under-a-mandate-of-its-own.md)): D-55 carries an inline mark (the launcher of the home environment also serves orders with no environment), and D-19, frozen and not edited, now means the home launcher's forge role, the control plane's own forge credential being read-only. *Proposed* (the ✅ rows D-17 and D-19 are not edited; the 🟡 rows D-10, D-43, D-46 and D-50 to D-53 carry inline marks and D-11 only this note): D-17, frozen (the launcher classifies production from its own configuration); D-19, frozen (a limit on Orchestrarr's own identity on the launcher's files); D-10 and D-11 (a card may offer a step that the launcher refuses, the launcher then publishes the grants it enforces and the control plane intersects them for display only: D-60, ADR 0015 §2). See D-60, D-61 and [ADR 0015](adr/0015-where-the-launcher-gets-what-its-checks-read.md).

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
| 🟡 D-10 | **Powers are granted per application and per environment, and are off until the operator enables them** (once the launcher exists, in two places: the control plane, for what a card offers, and each launcher, for what runs; the effective power is the intersection: proposed, D-60). An application is eligible for automatic deployment only if the environment description lets Orchestrarr know *how it is deployed*, *how to check it* and *how to roll it back* (D-17). Otherwise the card offers fewer steps (for example merge only). |
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
| 🟡 D-40 | **Who orders is authenticated by an OpenID Connect identity provider** (Keycloak in the reference setup): approving needs the operator role (a member of the operator group, D-54); approving a work item that touches production needs a **step-up** (a fresh second factor). |
| 🟡 D-41 | **A launch is a signed, single-use, time-limited order built by the server** from the plan the operator ticked, checked and executed by a **launcher** that is the only thing able to start an agent. No endpoint accepts a free command or a free prompt; the agent cannot start a run. |
| 🟡 D-42 | **No standing credential.** The dedicated Vault (D-44) issues credentials for one run and one environment at launch (secure shell certificate, cluster token, GitHub App token, the model credential); the machines accept only certificates of that authority for the operator principal. |
| 🟡 D-43 | **The product fails closed and has a kill switch**: no audit sink, no order; the kill switch stops the launcher and the signing and revokes the active leases. A local stop on each launcher's machine, and a quiet period of each launcher after it starts, are proposed (D-60). |
| 🟡 D-44 | **A dedicated Vault, separate from every other Vault, tied to the identity provider.** It is deployed by its own infrastructure-as-code repository outside Orchestrarr (platform layer, configuration layer); people sign in through the identity provider's OpenID Connect, groups map to policies. [ADR 0007](adr/0007-a-dedicated-vault-and-a-small-control-plane.md). |
| 🟡 D-45 | **One identity per component, with the right to sign split from the right to obey**: the control plane can only sign an order (non-exportable key), the launcher can only verify it and request the credentials it names, the agent run authenticates to nothing. No single compromised component can both sign and execute. |
| 🟡 D-46 | **The control plane is small and only launches.** The API decides and signs; the **launcher** has no inbound port and **pulls** signed orders from the order store, through the control plane's API (proposed, D-60); the agent run acts. An attacker cannot call the launcher, and only a signed, unexpired, unused order within the granted powers makes it do anything. The launcher takes its granted powers and its notion of production from its own configuration, never from that API (proposed, D-60, ADR 0015). |
| 🟡 D-47 | **Whoever gives an order is authorized twice and is always a person**: the API checks the session opened with the identity provider (the operator group, step-up on production) before signing, the launcher checks the signature and the powers again. A service client cannot hold the operator role (it cannot sign in, D-54). |
| 🟡 D-48 | **The dedicated Vault runs on the production cluster, like every production element**, deployed by the cluster's GitOps repository and configured by its own infrastructure repository. It starts sealed and is unsealed deliberately; the credential issued to a run for the cluster excludes the Vault's namespace. [ADR 0007](adr/0007-a-dedicated-vault-and-a-small-control-plane.md). |
| 🟡 D-49 | **A fifth review agent, the `spec-reviewer`, keeps the documentation true**: it compares the README (the entry point), the docs, the spec and the ADRs with the implemented state, in both directions, gives the exact text to write, and runs with the other four on every pull request. [ADR 0008](adr/0008-a-fifth-agent-keeps-the-documentation-true.md). |
| 🟡 D-50 | **Releases are decided by semantic-release on `main`, which takes merge commits only**: the version comes from the Conventional Commits, the image is tagged with it, and the cluster's GitOps deploys the newest version tag. A squash of the release pull request would hide the commits, so the repository forbids it. [ADR 0009](adr/0009-releases-are-decided-by-semantic-release-from-main.md). The launcher's image is proposed to be pinned by digest and changed by a person, an exception to the newest tag (D-60, ADR 0015). |
| 🟡 D-51 | **The parameters of an order, as the skeleton enforces them** (all configuration): an order lives 5 minutes, the launcher refuses more than 10 whatever the order says, a clock tolerance of 30 seconds applies to the issue time only, a second factor is fresh for 5 minutes. The server decides what production is from the environment description (an unknown environment is production) and signs the second factor's time into the order, so the launcher checks it again. An order is for one launcher (its audience). The signed form is length-prefixed (ADR 0006, canonical form v2). Missing, unreachable or unknown always means refused. *(Proposed changes for the launcher: D-60, ADR 0014 point 6, ADR 0015 §2.)* |
| 🟡 D-52 | **Approving records a decision, not an order**: this pull request at this commit, these steps, this approver. The client sends the work item, the steps it ticked and **the pull requests and commits it displayed**; the server decides everything else and refuses (`planChanged`) unless what was displayed is exactly what is there now, so an approval never covers a commit that arrived after the operator looked. A draft pull request is never approved. A step is approved only if it is offered, granted, recorded by the vertical and, for a merge, validated first. The order is issued at run start (proposed: at delivery to the launcher, D-60, ADR 0015). [ADR 0010](adr/0010-approving-records-a-decision-and-the-sandbox-operator.md). |
| 🟡 D-53 | **Powers are granted in the configuration** (`Powers:Grants`), off until listed, checked when the host starts, and a grant on every repository may only validate outside the sandbox. **There are two approvers: a person signed in through the identity provider (D-54), and, for the local sandbox only, a sandbox operator.** The host refuses the sandbox operator outside the Development environment and unless it listens on the loopback; in that mode the two routes that record something answer only to a local request (a `Host` of `localhost` or a loopback address, from a client that is itself on the loopback: from another machine, through a tunnel that ends there), and the image does not carry the sandbox's settings. Bounded stores, in memory or in a file (D-58): a full queue or trail refuses, it never forgets; refusals have a smaller trail budget than decisions. Once the launcher exists it holds its own copy of the grants and the effective power is the intersection (proposed, D-60). [ADR 0010](adr/0010-approving-records-a-decision-and-the-sandbox-operator.md). |
| 🟡 D-54 | **People sign in through the identity provider, in the host**: the authorization code flow with PKCE as a confidential client, a session cookie and no token in the browser; everything the API says is for a person who signed in; an operator is a member of a configured group; the addresses come from the configuration; a change must come from the application's own origin ([ADR 0011](adr/0011-people-sign-in-through-the-identity-provider.md)). Refines D-40. |
| 🟡 D-55 | **One launcher and one Vault role set per environment.** Vault answers only to the workload identity of the launcher of that environment, which reaches only that environment's roles: a compromised launcher yields one environment; the order is addressed to the launcher of its environment. The control plane never issues a credential (D-45). [ADR 0012](adr/0012-one-launcher-and-one-vault-role-per-environment.md). *Chosen by the maintainer.* *(Amended 2026-11-04, D-63: the launcher of the environment where the control plane runs also serves the orders that have no environment.)* |
| 🟡 D-56 | **The person chooses a tier of the operator account when approving, and the signed order carries it** (read-only, deploy, maintenance), mapped by each machine to a local account of its own; the operator account of D-34 stays the highest tier. Refines D-34 and D-42, tempers D-39. Safe default, to change if wanted. ADR 0012. |
| 🟡 D-57 | **For production the plan is validated before it is applied**: the agent proposes the diff, the person validates it, then it is applied. Who validates stays open (Q-21). Safe default. ADR 0012. |
| 🟡 D-58 | **The approvals, the audit trail and the global stop are kept in a SQLite file** (`Storage:Provider=Sqlite`, the image's default), the database itself enforcing the uniqueness of a decision and the append-only trail. **Unknown means engaged**: a stop whose state cannot be read counts as engaged, and one that cannot be released for certain (saved and traced) stays engaged. **A database created outside Development starts with the stop engaged.** An approval and its trace are one write; the capacities depend on the storage (500 and 2,000 in memory, 10,000 and 200,000 in a file); the file lives in `/config`, on a local disk, with a single replica; unset, the storage is memory in Development and the host refuses to start otherwise when approving is on; approving is limited per person (`Approvals:ApprovalsLimit` per window), and the same person refused for the same reason is traced once every ten minutes. [ADR 0013](adr/0013-approvals-the-trail-and-the-stop-are-kept-in-a-sqlite-file.md). |
| ✅ D-59 | **Decided, not built yet. The launcher is a Rust binary, held to the control plane by shared conformance vectors.** A static binary in an image that holds nothing else, no inbound port; the control plane stays .NET. The contract between the two is the signed order, written once (the canonical form, the checks in order with their refusal codes (those of Core today, extended by proposals), the defaults of the options, the audit entries, what a decision spends); it is held by data files (`contract/launch/`) produced by the C# side, committed, and run by both test suites. **A change to the contract is one pull request that changes the C# code, the Rust code and the vectors together.** The C# `Launcher` in Core stays as the reference and the oracle of the vectors, and runs in no process. **The ✅ covers only this.** Everything else is proposed: D-60 and the status of ADR 0014. [ADR 0014](adr/0014-the-launcher-is-a-rust-binary-held-to-the-control-plane-by-shared-vectors.md). *Chosen by the maintainer.* |
| 🟡 D-60 | **Where the launcher gets what its checks read** (safe default, to change if wanted; [ADR 0015](adr/0015-where-the-launcher-gets-what-its-checks-read.md) has the detail and lists the decisions it amends). The launcher **pulls its orders through the control plane's API**, as a delivery to the launcher of its own environment, and opens no port (D-46). It stays an authorization independent of the one that signed: its grants, production flags and audience are its **own configuration**, which Orchestrarr cannot change on its own; its nonce ledger is **in memory**, held by an exclusive claim, a quiet period, an issue-time floor and a synchronized clock; the global stop is read from the control plane (unknown means engaged); its trace goes through the API and, hash-chained, to its standard output; an engaged stop reaches a launcher within the stop statement's maximum age (the local stop is immediate). **The first slice is a dry run** that starts nothing. How it authenticates is D-62. |
| 🟡 D-61 | **The coding agent never merges, auto-merges or approves a pull request that touches the launch contract files** (the whole list is in [ADR 0015](adr/0015-where-the-launcher-gets-what-its-checks-read.md) §3: among others the launcher, the contract, the oracle and the grant matching in Core, the images' build files, the rules of CLAUDE.md, the code owners, the secret-scan settings and the review gate); the maintainer merges them, whatever their size. Proposed; followed by the agent as a precaution (rule 15 of CLAUDE.md), enforced by nothing but its instruction until the code owners and a ruleset exist. |
| ✅ D-62 | **The launcher authenticates to the control plane's API with a signed token of the identity provider, one client per launcher** ([ADR 0016](adr/0016-the-launcher-acts-under-a-mandate-of-its-own.md)): the client credentials flow with a signed assertion (`private_key_jwt`), a key pair generated where the launcher runs and never leaving it, a token that lives minutes, an audience that is the control plane's API and a `launcher` role that can never be an operator (D-47) and reaches only the routes for machines (their detail is Q-27), which a person's session cannot reach either. The launcher verifies the API through TLS against the certificate authority it is configured with, and plain HTTP is refused. Revoking a launcher is disabling its client. The token is a mandate to ask for the orders addressed to its own environment, not a power: the launcher still verifies the signature and its own grants. **The token gives no access to Vault**: the launcher gets the credentials to act from Vault under its workload identity (D-55); whether that is the same client of the identity provider is open (ADR 0016). *Chosen by the maintainer, on condition that the launcher pulls through the control plane's API (D-60, proposed): if that changes, D-62 is revisited. The ✅ covers the authentication; the signed stop statement and the machine routes are proposed (D-60, Q-27).* |
| ✅ D-63 | **The launcher of the environment where the control plane is installed also serves the orders that have no environment**, the work done through the forge (merging a pull request; the workshop run that validating needs is Q-28): it lists "no environment" among what it serves, with a forge credential of its own (a GitHub App token for one repository, from a Vault role of its own) and, as ADR 0015 §2 proposes, its own grants and production list per repository (proposed, with the exception to D-17 it needs), and no other launcher serves them. An action is always delegated to a launcher, and a launcher acts on the environment where it runs, outward only. This amends D-55 and replaces the launcher of its own that Q-26 proposed. [ADR 0016](adr/0016-the-launcher-acts-under-a-mandate-of-its-own.md). *The control plane's own forge credential is read-only: the right to merge exists only in the home launcher's forge role, which is what D-19's "Orchestrarr's identity" means from then on. The home launcher runs in a namespace of its own, with a service account of its own, and the forge's Vault role is bound to its workload identity only: the condition of the decision, which follows from D-45.* *Chosen by the maintainer, on the same condition as D-62 (D-60). Whether such an order is production is decided per repository (the launcher from its own list, the control plane from the environment description, D-17), an unknown repository being production: a proposed default (ADR 0016).* |

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

One container for the control plane (the launcher has an image of its own, ADR 0014), API first; the UI consumes the REST API (*arr pattern). Pluggable providers:

| Provider type | Role | v1 candidate |
|---|---|---|
| Findings source | Gives findings (D-03) | Portarr (API / MCP) |
| Environment description | Gives environments, runtimes, workloads, applications and the sources of their code and deployment (D-17, D-18) | Portarr (reference); a YAML structure file for standalone use |
| Forge | Branches, pull requests, merges, CI status | GitHub |
| Agent runtime | Runs the worker agent | Claude Code, non-interactive, in a container on a workshop host (✅ Q-07, D-24, D-25, ADR 0004) |
| Deployer | Redeploys and rolls back one application, chosen from what the environment description shows (runtime kind, deployment source) | ❓ Q-03 |
| Verifier | Smoke test after deployment | ❓ Q-06 (Portarr health checks are the candidate) |
| Memory | Runbooks read before acting, outcomes written back | Mnemarr |
| Launch (Core) | The **issuer** (control-plane side: authorizes, builds, signs) and the **launcher** (verifies, cannot sign: it has no signer), joined only by the signed order. Today `LauncherOptions` and the issuer's options default the audience to `launcher`; row 3a would remove that default (ADR 0014 point 6, proposed). In Core, the launcher is the **reference** implementation and the oracle of the conformance vectors; the one that will run is the Rust binary below (D-59, not built). Ports: signer, verifier (asymmetric), nonce ledger, kill switch, audit sink, granted powers, environment classifier. Faked in tests, except the kill switch, the audit sink and the granted powers, which have implementations (in memory or in a SQLite file, D-58) used by the approval vertical (D-53); no signer, verifier or ledger yet | ❓ Q-23 for the Vault adapter |
| Launcher (Rust, `launcher/`) | The process that pulls signed orders through the control plane's API, verifies them with the same rules as Core's launcher, and starts the run. One per environment (the one of the home environment also serves the orders with no environment, D-63), no inbound port, a signed token of its own towards the API (D-62), a static binary in an image that holds nothing else (D-55, D-59, D-60). Held to Core by shared conformance vectors in `contract/launch/` (ADR 0014). **Not built**: the first slice is a dry run | the Rust crates are chosen when it is written; its identity towards the API is D-62 (ADR 0016) |
| Approvals (Core, host) | The approval store, the audit trail and the global stop, behind `IApprovalStore`, `IAuditLog` and `IKillSwitchControl`; the routes depend on the interfaces | in memory (the sandbox) or in a SQLite file (D-58), bounded by the storage, refuse when full (D-53); approvals and their trace written together (`IApprovalLedger`) |
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
| F-SAF-01 | Global stop, dry-run mode, budgets (cost, time, concurrency). Built: the global stop; the rest comes with the launcher, with a local stop on each launcher's machine (proposed, D-60, ADR 0015). The dry run of ADR 0015 is the launcher's first slice, not the operator's dry-run mode, which stays to be specified |
| F-SAF-02 | Full audit trail of prompts, tool calls, decisions and approvals. Built: approvals, refusals and the global stop, in memory or in a SQLite file, append-only (Activity screen); the rest comes with the workshop |
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
- **Approvals fail closed** (D-52, D-53): no approval without its trace, none on a list of pull requests that is not read, changed since it was displayed, that repeats one or that only has drafts, none for a step that is not offered, granted or recorded, none for a merge without validation, none while the global stop is engaged; every refusal is traced with its code (the same person refused for the same reason once every ten minutes, and attempts above `Approvals:ApprovalsLimit` are answered 429 without a trace, ADR 0013), engaging the stop works even when its trace cannot be written while a release must be saved and traced, otherwise the stop stays engaged (D-58), and the trail keeps room for decisions and stops when refusals pile up.
- **The API is for people who signed in** (D-54): with `Approvals:Mode=OpenIdConnect` every route of the API needs a session (401 without one, never a redirect; the files of the interface stay public), only members of the operator group approve or stop, and a change must come from the application's own origin (`originNotAllowed`). The host does not start without an https authority and public address (http only on the loopback or a private network address, in Development). `GET /health` is the only anonymous route beside the sign-in's own, and says nothing.
- **Every answer, in every mode,** forbids being framed, carries `nosniff` and a `Content-Security-Policy` that keeps the interface's scripts, styles and requests to itself (D-54, ADR 0011); with the sign-in on and an https public address it also carries `Strict-Transport-Security`.

---

## 8. Roadmap 🟡

| # | Vertical | Status |
|---|---|---|
| 1 | **Intake from Portarr and read-only decision cards** (F-INT-01, F-INT-02, F-CRD-01), before any write power exists | built, reviewed in the sandbox by the review agents (four at the time). The *verdict* half of F-CRD-01 and the eligibility of *deploy* (a known check and rollback, D-10) come with later verticals, so deploy is shown as a preview. Reading Portarr every 30 seconds is the interim answer to VISION question 8 (poll or webhook) |
| 2 | **Read the dependency pull requests and their checks from the forge** (F-INT-04); Portarr only gives their number | built, reviewed. Read-only, GitHub; the next vertical validates and merges them |
| 2b | **Control-plane skeleton**: signed launch orders, the issuer that authorizes whoever orders, the launcher checks, refusal codes (D-41, D-45 to D-47, D-51; ADR 0006, 0007) | built in Core, reviewed by the five agents, **the ports of the kill switch, the audit sink and the granted powers are wired in the host by the approval vertical (rows 2c and 2e)**; the issuer, the launcher, the signer, the nonce ledger and the environment classifier are not: no store the launcher pulls from, no Vault adapter, no lease, concurrency or budget check, no order endpoint (the store behind the API and the pull come with row 3a). It starts nothing and grants no power |
| 2c | **Approve a decision card**: the tick becomes a recorded approval per pull request and commit (F-CRD-01), with the queue, the audit trail (F-SAF-02) and the global stop (F-SAF-01), in memory at first (D-52, D-53; ADR 0010) | built, reviewed by the five agents. No launcher takes the approvals yet: nothing runs. The persistence is row 2e; the sign-in is row 2d |
| 2d | **Sign in through the identity provider** (D-40, D-54, Q-12; ADR 0011): the session cookie, the operator group, the reads of the API behind it, the origin check, the interface that signs in by itself | built, with a local Keycloak for the demonstration (`samples/keycloak`). The step-up is read, not asked for; a session is not revoked before it ends (ADR 0011). A single replica: the session is a cookie, and its keys are the host's |
| 2e | **Keep what must survive a restart**: the approvals, the audit trail and the global stop in a SQLite file; the stop starts engaged when it cannot be read, or when its database is new outside Development (D-58; ADR 0010, 0013) | built, reviewed by the five agents. The lifecycle of an approval (taken, done, expired, superseded) comes with the launcher that takes it |
| 3a | **The launcher, in Rust, and the conformance vectors** (D-46, D-55, D-59 to D-63; ADR 0007, 0012, 0014 to 0016). Grants no power. In Core first: the quiet period, the clock check, the issue-time floor and the served environments in `Launcher`, no default audience, the issuer addressing an order to the launcher of its environment (with an explicit entry for "no environment" and no default), the issuer and `Launcher` classifying an order with no environment per repository, an unknown one being production (ADR 0016), the lifecycle of an approval (ADR 0013). Then: the vectors produced by Core and committed, the C# and the Rust launchers both held to them, the order store behind the control plane's API, the pull, the test that Core's launcher is registered by no host, the Rust lane as a required check in the repository's settings, the rule for when an order is signed and re-issued, and a **dry run** that starts nothing and writes to the audit trail what it would have started | not started: next, as several pull requests (the review gate restored from the base, ADR 0015 §3; the approval lifecycle, the Core changes with the vectors, the Rust launcher, the delivery API and the audit route, the dry run). The steps that can be approved today carry no environment: the launcher of the home environment runs them (D-63). Needs Q-27 before the vectors are generated, with the step-up's freshness settled against signing at delivery and the quiet periods. The control plane is meant to refuse to deliver orders while its storage provider is `Memory` outside Development (ADR 0013) |
| 3b | **Validate and merge**, with the workshop (D-24, D-25; ADR 0004, 0007, 0010); uses 2b to 2e and 3a | not started: after 3a, and after Q-28 (who starts a workshop run). The first write power |
| 4 | Deploy, smoke test and roll back (F-RUN-03), with the regression report (F-RUN-04) | after 3b |
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
| ❓ Q-10 | **Allow-list of repositories**, and whether infrastructure-as-code and container definitions are in scope. Whatever the answer, the repository that holds a launcher's configuration is never one Orchestrarr can change on its own (ADR 0015). |
| ❓ Q-11 | **The existing backlog of dependency pull requests.** Order, conflicts between pull requests of one application, grouping. |
| 🟡 Q-12 | **Authentication** for the UI and the API. Proposed resolution for people: D-54, in the host, with the identity provider. Left open and not needed yet: API keys for machines (a service account cannot be an operator, D-47; the launcher's part of it is D-62, a signed token per launcher (ADR 0016)), and the link in a mail, which opens the page and signs in first. |
| ❓ Q-13 | **Standalone mode**: manual work items when no Portarr is connected (VISION.md §3). |
| ✅ Q-14 | **Merging under required-review rulesets.** Resolved: Orchestrarr's identity may approve its own pull requests; accounts, tokens and rulesets are set up accordingly (D-19). *(2026-11-04: the identity that may approve and merge is, once the launcher exists, the forge role of the home launcher, the control plane's own credential being read-only: D-63, ADR 0016.)* |
| 🟡 Q-15 | **Structure file format** for standalone mode. Proposed: a subset of Portarr's catalog plus a `deployment` key ([environment-file.md](environment-file.md), D-20). To confirm when the first vertical reads it. |
| ❓ Q-16 | **How does Orchestrarr act on a machine?** A connection per machine (SSH, like Ansible), configured, never in a worker. Open: where the key lives, whether commands are free or only declared actions (playbooks run by Orchestrarr's own code), and whether an agent in a container may run commands through that connection (Claude Code in a container with its own narrow credential), or only propose them. Recommendation: diagnosis free but read-only, changes through the declared state (D-32), a free command only per machine and per approval. The maintainer's direction of 2026-11-03: the agent acts as Claude Code would, without a container, with no initiative (D-33 to D-35). |
| ❓ Q-17 | **What does a backup finding look like?** Which backup tools and which signals (last success too old, never ran, failed run). Portarr already reads the backups of a Backrest instance and reports unprotected data; the question is how much of that is enough to name a cause, and what a repair of each looks like (a configuration, a permission, a full disk, a credential). |
| ❓ Q-18 | **What is a "dependency pull request"?** Portarr counts Dependabot's; Orchestrarr reads the bots of its configuration (Dependabot and Renovate). Should Portarr count every bot, or expose the list (D-18)? Until then the two numbers can differ and the screens say so (ADR 0005). |
| ❓ Q-19 | **Where does the agent process run when it acts on an environment?** On the environment itself (the model credential would have to be there, on every machine), or on the workshop, which opens a session on the environment (the long-lived subscription token stays on one machine and is handed to the run only). Recommendation: the second, with the journal on the environment. |
| ✅ Q-20 | **Vault or a lighter authority?** Resolved: a new Vault dedicated to this purpose, tied to the identity provider, provisioned by its own repository (D-44, ADR 0007). Its issued SSH certificates replace any SSH authority of its own. |
| ❓ Q-21 | **Who approves production?** One operator with a step-up, or two people for what cannot be undone. |
| ✅ Q-22 | **Where does the dedicated Vault run, and how is it unsealed?** Resolved: on the production cluster (D-48), integrated storage, one node, unsealed by hand with key shares the maintainer holds; the unseal never becomes automatic.
| ❓ Q-23 | **How do clients reach the Vault, and over what?** Today: a plain HTTP listener inside the cluster, reachable only by namespaces that opt in, and a port-forward for people. Open: TLS (a certificate authority in the cluster, or the ingress), and a way in for the launcher when it runs outside the cluster.
| ❓ Q-24 | **How long is the audit trail kept, and can a person be erased from it?** Today the trail is append-only and refuses new approvals when full (200,000 entries in a file). Archiving, signing or shipping it elsewhere, and erasing a person on request without breaking the append-only rule, are not decided (ADR 0013). |
| ✅ Q-25 | **How does the launcher authenticate to the control plane's order API, and how does it know the API is the real one?** Resolved: a signed token of the identity provider, one client per launcher, with a key that never leaves it; TLS verified against a configured certificate authority (D-62, [ADR 0016](adr/0016-the-launcher-acts-under-a-mandate-of-its-own.md)). The signed stop statement stays proposed (D-60, ADR 0015 §5, Q-27), and so does the rotation of the key (Q-27). |
| ✅ Q-26 | **Who runs an order with no environment?** Resolved: the launcher of the environment where the control plane is installed, with an entry of its own for it (D-63, [ADR 0016](adr/0016-the-launcher-acts-under-a-mandate-of-its-own.md)). It amends D-55. |
| ❓ Q-27 | **Machine routes, the stop statement and where the launcher runs.** ADR 0015 leaves open: the allowed identity, rate and size of each route a machine uses (delivery, decision, stop, published grants, audit); the form, key usage, audience, sequence and maximum age of the signed stop statement; where the launcher runs (cluster or workshop host) and the exclusive-claim mechanism outside a cluster. The placement of the home launcher (in the environment where the control plane is installed, D-63) is settled; the others stay open. The rotation of a launcher's key and where it is kept are settled with them (D-62; ADR 0016: a key is registered by a person or by the identity provider's infrastructure-as-code, never at run time by the launcher or the control plane). To settle with row 3a, before the vectors are generated. |
| ❓ Q-28 | **Who starts a workshop run, and what does "validate" mean for a launcher?** Validating a pull request means building and testing it in a Claude Code run on a workshop (D-24, D-25), a machine separate from the target environments. The home launcher acts on the environment where it runs and reaches outward only (D-63), so it does not start a container on another machine, and no launcher serves that step today. Options: the workshop is an environment with a launcher of its own; a launcher on the workshop host serves the orders with no environment that need a workshop; or "validate" only reads the forge's CI status. Needed before row 3b, and before the dry run if its first order is a validation. To be decided by the maintainer. |

---

## 10. Glossary

| Term | Meaning |
|---|---|
| Work item | A "chantier": one unit of maintenance work on one repository, or on one third-party image (D-21). |
| Decision card | The few-checkbox page where the operator approves a work item. |
| Verdict | The summary of what changed and how it was checked. |
| Smoke test | The minimal check that a redeployed application starts and answers. |
| Conformance vectors | The data files, in `contract/launch/`, that both the C# and the Rust implementations of the launch rules must satisfy: an order and the state its checks read, in; the decision and its trace, out (D-59). |
| Launcher | The small component, one per environment, that verifies a signed order and starts the agent run; decided to be a Rust binary with no inbound port (D-46, D-59), **not built yet**: today it exists only as the reference library code in Core. |
| Home environment | The environment where the control plane is installed. Its launcher also serves the orders that have no environment, such as a merge (D-63). |
| Launch contract files | The list in [ADR 0015](adr/0015-where-the-launcher-gets-what-its-checks-read.md) §3 (the launcher, the contract, the oracle in Core, the images' build files, the rules and the review gate): the pull requests that touch them are merged by the maintainer (D-61, rule 15 of CLAUDE.md). |

---

## 11. Changelog

| Date | Change |
|---|---|
| 2026-11-04 | v0.12: the launcher authenticates with a signed token of its own, one client per launcher, and its token gives no access to Vault (D-62, Q-25 resolved); the launcher of the home environment also serves the orders that have no environment, which amends D-55 (D-63, Q-26 resolved); [ADR 0016](adr/0016-the-launcher-acts-under-a-mandate-of-its-own.md); Q-27 keeps the machine routes and the rotation of the key; Q-28 opens who starts a workshop run. |
| 2026-11-04 | v0.11: the launcher is a Rust binary held to the control plane by shared conformance vectors, and pulls its orders through the control plane's API, dry run first (D-59, D-60, D-61, with a proposed exception to D-17 for the launcher and a limit on the coding agent; Q-25 to Q-27; ADR 0014, and ADR 0015 as a proposal; D-10, D-43, D-46 and D-50 to D-53 annotated; the proposed amendments to D-11, D-17 and D-19 are listed at the head of the decisions (the amendment of D-55 was decided later, v0.12, D-63)); the roadmap's row 3 is split into 3a (launcher and vectors) and 3b (validate and merge). |
| 2026-11-03 | v0.10: the approvals, the audit trail and the global stop are kept in a SQLite file; an unreadable stop, or a new database outside Development, counts as engaged; a release is saved and traced; Q-24 opened (D-58; ADR 0013). |
| 2026-11-03 | v0.9: one launcher and one Vault role set per environment, tiers of the operator account, the plan validated for production (D-55 to D-57; ADR 0012); the order is secured as shown in security-model.md. |
| 2026-11-03 | v0.8: people sign in through the identity provider, in the host (D-54, a proposed resolution of Q-12 for people; ADR 0011). |
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
| 2026-11-01 | v0.1: first interview. Decisions D-01 to D-09 and D-14 frozen; D-10 to D-13 proposed. |
