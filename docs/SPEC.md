# Orchestrarr — Functional and Technical Specification

> **Status**: draft v0.6. The first vertical (read-only) is built; no power that acts exists yet.
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
| 🟡 D-22 | **The server sends data and codes; the interface words them.** Findings, plan steps, reasons and source errors are codes and numbers: no sentence travels in the API, because the contract outlives one client. No exception text reaches a client: a failed read gives a code (`unreachable`, `unauthorized`, `timeout`, `invalidResponse`, `failed`) and the details stay in the log. |
| 🟡 D-23 | **"Available" and "granted" are two things.** `available`: possible, from what the source says. `granted`: allowed by the powers granted to Orchestrarr (D-10, D-11). No power exists yet, so no step is granted and a card is a preview. A scan that is off or unreadable is a finding, not a clean repository. |
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
| 🟡 D-39 | **The security of the agent's power is in who can cause a launch, not in limiting what the launched agent can do.** On its environment the agent is omnipotent, on production virtual machines, never on the maintainer's own machine. [ADR 0006](adr/0006-a-launch-is-a-signed-order-and-credentials-are-short-lived.md). |
| 🟡 D-40 | **Who orders is authenticated by Keycloak** (OpenID Connect): approving needs the operator role; approving a work item that touches production needs a **step-up** (a fresh second factor). |
| 🟡 D-41 | **A launch is a signed, single-use, time-limited order built by the server** from the plan the operator ticked, checked and executed by a **launcher** that is the only thing able to start an agent. No endpoint accepts a free command or a free prompt; the agent cannot start a run. |
| 🟡 D-42 | **No standing credential.** The dedicated Vault (D-44) issues credentials for one run and one environment at launch (secure shell certificate, cluster token, GitHub App token, the model credential); the machines accept only certificates of that authority for the operator principal. |
| 🟡 D-43 | **The product fails closed and has a kill switch**: no audit sink, no order; the kill switch stops the launcher and the signing and revokes the active leases. |
| 🟡 D-44 | **A dedicated Vault, separate from every other Vault, tied to the identity provider.** It is deployed by its own infrastructure-as-code repository outside Orchestrarr (platform layer, configuration layer); people sign in through the identity provider's OpenID Connect, groups map to policies. [ADR 0007](adr/0007-a-dedicated-vault-and-a-small-control-plane.md). |
| 🟡 D-45 | **One identity per component, with the right to sign split from the right to obey**: the control plane can only sign an order (non-exportable key), the launcher can only verify it and request the credentials it names, the agent run authenticates to nothing. No single compromised component can both sign and execute. |
| 🟡 D-46 | **The control plane is small and only launches.** The API decides and signs; the **launcher** has no inbound port and **pulls** signed orders from the store; the agent run acts. An attacker cannot call the launcher, and only a signed, unexpired, unused order within the granted powers makes it do anything. |
| 🟡 D-47 | **Whoever gives an order is authorized twice and is always a person**: the API checks the identity provider's token (operator role, step-up on production) before signing, the launcher checks the signature and the powers again. A service client cannot hold the operator role. |

---

## 3. Use cases

| # | Use case |
|---|---|
| UC-01 | I get an email from Portarr: "3 findings need a decision". One click opens the decision card in Orchestrarr. |
| UC-02 | On the card, each finding shows what changes, what was tested, the risk, and the planned steps, all ticked. I confirm, or untick *deploy* and only merge. |
| UC-03 | I have a backlog of dependency-bot pull requests. Orchestrarr groups them per application, validates each one (build, tests), and proposes them on cards instead of leaving them to rot. |
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
| Notifier | Tells the operator | Portarr sends (D-03); Orchestrarr shows status in its UI |

Today one interface (`IFleetSource`) gives both the findings and the environment, because Portarr's maintenance report carries both. It will be split into a findings source and an environment description when the structure-file provider (F-INT-03) needs it; the host, not a provider, chooses which source is used.

Screens in *arr style: **Queue** (running and waiting), **Decisions** (cards awaiting approval), **History**, **Activity** (audit trail), **Settings** (applications and powers, budgets, kill switch).

---

## 6. Features (draft)

| # | Feature |
|---|---|
| F-INT-01 | Read findings and the environment of each application from Portarr |
| F-INT-02 | Group findings into work items, rank by severity |
| F-INT-03 | Environment-description provider reading a YAML structure file (standalone mode, D-18) |
| F-CRD-01 | Decision card with ticked steps and a verdict |
| F-CRD-02 | Signed, expiring, single-use link to a card (D-12) |
| F-RUN-01 | Run an agent in an isolated clone with a narrow credential and a budget |
| F-RUN-02 | Validate a dependency pull request: build, tests, regression check |
| F-RUN-03 | Merge when ticked, deploy per environment when ticked, smoke test, rollback |
| F-RUN-04 | **Regression report** of every repair: the tests before and after (counts, names of those that changed state, durations), the lint and the scan results, the finding that was targeted (gone or not), the files changed and the coverage of the change. It is part of the verdict of the decision card and of the pull request (D-30) |
| F-ENV-01 | **Machines Orchestrarr may act on**, each with its own connection (SSH first): user, key by reference, the actions allowed. Off until the operator enables them (D-31) |
| F-ENV-02 | **Diagnose on a machine**: read-only commands, to find why something fails (a backup, a service) |
| F-ENV-03 | **Act on a machine**: a declared action, approved as a step of the card, with the output kept in the audit trail and a check that it worked (D-32) |
| F-BKP-01 | **Backups that fail or are missing are findings** (read from Portarr, which already follows the backups) and become work items like any other (UC-07) |
| F-SAF-01 | Global stop, dry-run mode, budgets (cost, time, concurrency) |
| F-SAF-02 | Full audit trail of prompts, tool calls, decisions and approvals |
| F-APP-01 | Powers per application and environment, off until enabled (D-10) |
| F-MEM-01 | Read runbooks from Mnemarr before acting, write outcomes back |

---

## 7. Safety 🟡

The principles of [VISION.md §5](VISION.md) apply, as amended by [ADR 0001](adr/0001-merge-and-deploy-on-operator-approval.md): Orchestrarr **merges and deploys on the operator's tick** (D-15). The tick *is* the human approval. In exchange, Orchestrarr holds credentials that can merge and deploy, which makes the following non-negotiable:

- Powers per application and per environment, off until enabled (D-10, D-11).
- A merge or deploy credential exists only for the duration of one approved step, scoped to one repository or one target.
- Branch rulesets on the forge stay the last line of defence.
- Rollback is a precondition of automatic deployment (D-08, D-10).
- Findings, changelogs and repository content are untrusted data, never instructions.

---

## 8. Roadmap 🟡

| # | Vertical | Status |
|---|---|---|
| 1 | **Intake from Portarr and read-only decision cards** (F-INT-01, F-INT-02, F-CRD-01), before any write power exists | built, reviewed in the sandbox by the four review agents. The *verdict* half of F-CRD-01 and the eligibility of *deploy* (a known check and rollback, D-10) come with later verticals, so deploy is shown as a preview. Reading Portarr every 30 seconds is the interim answer to VISION question 8 (poll or webhook) |
| 2 | Read the dependency pull requests and the CI of each repository from the forge (Portarr only gives their number) | next, read-only |
| 3 | Validate and merge: needs the agent runtime (Q-07) | blocked on Q-07 |
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
| ❓ Q-12 | **Authentication** for the UI and for the card links (single-sign-on in front, API keys for machines). Until it is decided the API has none and the image must sit behind an authenticating front; it must be settled before any route acts. |
| ❓ Q-13 | **Standalone mode**: manual work items when no Portarr is connected (VISION.md §3). |
| ✅ Q-14 | **Merging under required-review rulesets.** Resolved: Orchestrarr's identity may approve its own pull requests; accounts, tokens and rulesets are set up accordingly (D-19). |
| 🟡 Q-15 | **Structure file format** for standalone mode. Proposed: a subset of Portarr's catalog plus a `deployment` key ([environment-file.md](environment-file.md), D-20). To confirm when the first vertical reads it. |
| ❓ Q-16 | **How does Orchestrarr act on a machine?** A connection per machine (SSH, like Ansible), configured, never in a worker. Open: where the key lives, whether commands are free or only declared actions (playbooks run by Orchestrarr's own code), and whether an agent in a container may run commands through that connection (Claude Code in a container with its own narrow credential), or only propose them. Recommendation: diagnosis free but read-only, changes through the declared state (D-32), a free command only per machine and per approval. The maintainer's direction of 2026-11-03: the agent acts as Claude Code would, without a container, with no initiative (D-33 to D-35). |
| ❓ Q-17 | **What does a backup finding look like?** Which backup tools and which signals (last success too old, never ran, failed run). Portarr already reads the backups of a Backrest instance and reports unprotected data; the question is how much of that is enough to name a cause, and what a repair of each looks like (a configuration, a permission, a full disk, a credential). |
| ❓ Q-19 | **Where does the agent process run when it acts on an environment?** On the environment itself (the model credential would have to be there, on every machine), or on the workshop, which opens a session on the environment (the long-lived subscription token stays on one machine and is handed to the run only). Recommendation: the second, with the journal on the environment. |
| ✅ Q-20 | **Vault or a lighter authority?** Resolved: a new Vault dedicated to this purpose, tied to the identity provider, provisioned by its own repository (D-44, ADR 0007). Its issued SSH certificates replace any SSH authority of its own. |
| ❓ Q-21 | **Who approves production?** One operator with a step-up, or two people for what cannot be undone. |
| ❓ Q-22 | **Where does the dedicated Vault run, and how is it unsealed?** Recommendation: a small virtual machine of its own, outside the cluster it will help administer, integrated storage, Shamir key shares held by the maintainer (a restart needs a deliberate unseal, which is also the deepest kill switch), snapshots backed up off the host. The alternative, automatic unseal against another Vault, ties the two together. |

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
| 2026-11-03 | v0.6 (amended): a new Vault dedicated to this purpose and tied to the identity provider, the small control plane that only launches, the authorization of whoever gives an order (D-44 to D-47, Q-20 resolved, Q-22; ADR 0007). |
| 2026-11-03 | v0.6 (amended): the structure of security the maintainer asked for: who orders is authenticated (Keycloak), a launch is a signed order checked by a launcher, credentials are short-lived (D-39 to D-43, Q-20, Q-21; ADR 0006). |
| 2026-11-03 | v0.6 (amended): the maintainer's direction on acting on environments: the agent acts as Claude Code would, no container there, no initiative, a journal per environment (D-33 to D-35, Q-19; ADR 0004 revision). |
| 2026-11-03 | v0.6: the new scopes the maintainer named: the regression report of every repair (F-RUN-04, D-30), acting on machines through a configured SSH connection (F-ENV-01 to F-ENV-03, D-31, D-32), backups that fail as findings (F-BKP-01); use cases UC-07 to UC-09; roadmap row 5 (not started); Q-16 and Q-17. |
| 2026-11-03 | v0.5: the repair protocol: Claude Code in a container (D-24), the workshop host and the disposable workspaces (D-25), the worker never deploys (D-26), the subscription credential and the budgets in attempts and minutes (D-27), the restricted network (D-28), Sonnet for every run (D-29); Q-07 and Q-08 resolved, Q-09 proposed (ADR 0004). |
| 2026-11-03 | v0.4: the first vertical is built; its review made three decisions explicit (D-21 the unit of work, D-22 data and codes not sentences, D-23 available versus granted); Q-12 says the API has no authentication yet; roadmap and interface notes. |
| 2026-11-01 | v0.3: Orchestrarr may approve its own pull requests (D-19, Q-14 resolved); environment file format proposed ([environment-file.md](environment-file.md), D-20, Q-15); Q-03 narrowed to the missing deployment source. |
| 2026-11-01 | v0.2: merge and deploy on tick (D-15, ADR 0001), everything ticked by default (D-16), deployment observed not declared, Portarr as environment provider with a standalone structure file (D-17, D-18, ADR 0002). Q-01, Q-02 resolved; Q-03 reformulated; Q-14, Q-15 added. |
