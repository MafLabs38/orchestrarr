# Orchestrarr: functional specification

> **Status**: draft v0.1 (2026-11-13), written from the maintainer's own words in the sessions of 2026-11-12 and 2026-11-13. It is the **base of work**: the architecture is drawn from it, not the reverse. It starts over from the use cases of [USE-CASES.md](USE-CASES.md).
> **Scope**: what the product does, seen from the maintainer and from the systems around it. It says *what*, never *how it is built*. The architecture, the decision records (ADRs) and the existing [SPEC.md](SPEC.md) are reconciled with this document in a later pass; where they disagree, **this document wins** until then.
> **Legend**: **(M)** the maintainer said it. **(P)** the assistant proposes it to fill a gap; it is not settled until the maintainer confirms it. Nothing here is built unless a section says so.

## Contents

1. Purpose and principles
2. Vocabulary
3. Actors and their roles
4. The life of a problem
5. Functional requirements
   - 5.1 Finding problems
   - 5.2 The plan and its validation
   - 5.3 Missions
   - 5.4 The three worlds
   - 5.5 Mandates
   - 5.6 The controller
   - 5.7 Staging readiness
   - 5.8 Verification by the agent
   - 5.9 The frame and safety
   - 5.10 Complexity and notifications
   - 5.11 Failure, rollback and loops
   - 5.12 What the maintainer sees
6. The four reference cases, traced
7. What is expected from Portarr
8. The laboratory
9. Qualities the product must have
10. What happens to what exists
11. Decisions and open points

---

## 1. Purpose and principles

Orchestrarr keeps a fleet of applications healthy **by itself**. A problem is seen, a plan is validated once by the maintainer, and from then on an agent does the work of a developer, through staging and the Git pipelines to production, without being asked again. The maintainer is told what was done, and for a complex case is asked to try the application. **(M)**

The principles that every requirement below serves:

- **P1. The agent is a developer with a mission, not an automaton.** It is given a problem, never a script. It chooses what to read, what to change, which tests to write, in which order. **(M)**
- **P2. What is fixed is the frame, not the path.** The frame is what the agent may touch and where. Inside it the agent is free; the frame is enforced by the environment, not by the agent's good will. **(M)**
- **P3. The maintainer validates a plan, then touches nothing** in the great majority of cases. **(M)**
- **P4. Quality is the pipelines' verdict**, and, where an application has a staging, the verdict of tests run there. **(M)**
- **P5. Small and large are one mechanism.** Merging a bot's bump and refactoring a service are the same kind of work with a different weight. **(M)**
- **P6. A mission is defined by its place, not its content.** The work moves through **worlds** (staging, Git, release); each has its own rights, and the agent asks for a **mandate** to enter the next one. **(M)**
- **P7. Two agents cross-check.** The agent that works does not decide alone that it may go on: a second agent, the **controller**, reads the evidence and grants or refuses the mandate. **(M)**
- **P8. The product loops.** A defect found after a step opens a new mandate to repair it; loops are bounded. **(M)**
- **P9. The product is not deterministic by design.** No workflow of steps is written in advance for a kind of problem. **(M)**
- **P10. Safety stays**: a kill switch, bounded budgets, a complete audit trail, no direct push to a protected branch, a way back for what is deployed. **(P, from rule 5 of CLAUDE.md, which the maintainer accepted)**

---

## 2. Vocabulary

| Term | Meaning |
|---|---|
| **Fleet** | The applications, machines and environments that Orchestrarr keeps healthy. |
| **Perimeter** | What Orchestrarr is allowed to maintain, written in the description file. Outside it Orchestrarr touches nothing. |
| **Finding** | A fact reported by a source: a vulnerability, a version behind, a support that ended, a failing CI, a pull request from a bot. |
| **Problem** | A finding (or several linked ones) that Orchestrarr has decided is worth solving. One problem has one outcome to reach. |
| **Plan** | The list of problems to solve, each with the limits within which it may be solved. The maintainer validates it. |
| **Standing rule** | A validation given in advance for a whole class of problems ("everything deprecated is bumped, except the majors that break"). |
| **Mission** | A piece of work in **one world**, given to the worker agent, with an objective, a frame, a budget and an expected dossier. |
| **World** | A place with its own rights where a mission is done: the **staging** world, the **Git** world, the **release** world. |
| **Mandate** | The permission, issued by the control plane, to carry out a mission in a world. Scoped, time-limited, budgeted, recorded. |
| **Worker agent** | The agent that does the work of a mission. |
| **Controller** | The agent of the control plane that reads a finished mission's dossier and decides whether the next mandate may be granted. |
| **Dossier** | What a mission hands over: what changed, why, the evidence of the tests, the open points. The controller reads only this. |
| **Runner** | The process that provides a mission's workspace and enforces its frame, in the place where the world is. |
| **Workspace** | The isolated place where a worker agent works during a mission. |
| **Staging** | An environment that mirrors production, without real secrets or real side effects, where changes are proved. |
| **Staging profile** | What an application needs in order to be tested in staging, and whether that is available (section 5.7). |
| **Baseline** | The state in which an application, unchanged, passes its tests in staging. |
| **Frame** | The limits of a mission: the repositories and paths allowed, the forbidden ones (pipelines, secrets), the budget. |
| **Budget** | The bound on a mission: time, attempts, subscription quota. |
| **Complex** | A mark put on a case by the worker agent, which triggers a notification asking the maintainer to try the application. |
| **Laboratory** | The set of deliberately broken projects, with the staging, where cases are reproduced and judged. |

---

## 3. Actors and their roles

| Actor | Role |
|---|---|
| **The maintainer** | Validates the plan or a standing rule, receives notifications, tries the application after a complex case, can stop everything. The only human. |
| **Portarr** | Sees and describes the fleet; reports findings, the environments (staging included) and their state. It never acts. **(M)** |
| **Orchestrarr control plane** | Holds the plan, issues mandates, runs the controller, keeps the history, the audit trail and the kill switch. |
| **The worker agent** | Does a mission as a developer would. A model run (Claude Sonnet, on the maintainer's subscription). **(M)** |
| **The controller** | A second agent, in the control plane, independent of the worker; decides on mandates. **(M)** |
| **The runner** | Provides the workspace of a mission where the world is; enforces the frame; records the trace and the evidence. |
| **The forge** (GitHub) | Branches, pull requests, protection rules, code owners, CI/CD. It enforces much of the frame. |
| **The staging platform** | The hypervisor and its virtual machines where staging environments run. **(M)** |
| **Production environments** | Where applications really run. |
| **Mnemarr** | Long-term memory: what already broke, runbooks, past outcomes. |
| **The notifier** | Mail to the maintainer. |

---

## 4. The life of a problem

A problem goes through the same path whatever its weight; the path is a sequence of worlds, not of fixed steps. What happens **inside** a world is the agent's.

1. **Seen.** A source reports a finding (section 5.1). Orchestrarr decides it is a problem inside the perimeter.
2. **In the plan.** The problem appears in the plan with the limits it may be solved within. A **standing rule** may already cover it; otherwise the maintainer validates it. **(M)**
3. **Staging ready?** If the application has a staging requirement, Orchestrarr checks that staging is available and **proven healthy on the unchanged code** (the baseline, section 5.7). If not, no mandate on the code is issued; the maintainer is told why. **(M)**
4. **Mission in the staging world.** A mandate is granted. The worker agent works in a workspace in the staging machine: it deploys the application there, changes the code, rebuilds, redeploys, tests (requests, a browser, the real behaviour), and iterates until it believes the problem is solved. It hands over a **dossier**. **(M)**
5. **Controller, first crossing.** The controller reads the dossier and checks, without bias, that the contract was respected (section 5.6). If yes, it grants the mandate for the Git world; if no, it sends the mission back or ends it. **(M)**
6. **Mission in the Git world.** The worker agent opens the pull request, follows the CI/CD pipelines, fixes what they report, and merges into develop when the rules allow. It hands over a dossier. **(M)**
7. **Controller, second crossing.** The controller reads the new dossier (the pipelines' verdict, the merged commit). It grants the mandate for the release world, or refuses. **(M)**
8. **Mission in the release world.** The worker agent deploys to production through the way the environment is deployed, then verifies the result as a tester would, with evidence. If production does not behave, it rolls back. **(M, rollback from rule 5 of CLAUDE.md)**
9. **Closed, or looped.** If the verification is good, the problem is closed and a report is written. If a defect shows up later, **a new mandate** is asked to repair (P8); the loop is bounded (section 5.11). **(M)**
10. **Told.** The maintainer gets what is useful: nothing for a simple success, a notification for a complex case, a failure, an exposed secret, a loop that hit its limit. **(M)**

An application with **no staging** skips steps 3 to 5; it goes from the plan straight to the Git world, then the release world, with no net (decided for CASE-17). **(M)**

---

## 5. Functional requirements

### 5.1 Finding problems

**FR-FIND-1.** Orchestrarr reads the findings from **Portarr**, which is the product that sees. Orchestrarr does not observe the fleet itself. **(M)**
**FR-FIND-2.** The sources of findings that the cases need are: vulnerabilities in dependencies, images and containers; versions behind (dependencies, images, platforms); end of support (operating systems, platforms, runtimes); deprecations in the code; pull requests from bots; failing CI; exposed secrets; failing backups. **(M for the list of needs; which tool provides each is open, section 7)**
**FR-FIND-3.** Orchestrarr also reads, from the forge, what only the forge knows: the pull requests and their checks, and where in the code a version is written. **(M, built)**
**FR-FIND-4.** A finding outside the perimeter is shown as outside and is never acted on. **(M)**
**FR-FIND-5.** Several findings about the same cause become **one problem**; several bot pull requests on one repository are handled together when that makes one deployment instead of many (CASE-07). **(P)**
**FR-FIND-6.** A problem carries a **priority** that comes from its kind and its severity; a serious vulnerability goes first and notifies at once (CASE-03). **(M for the serious advisory; the rest P)**
**FR-FIND-7.** A problem that is already being worked on is not started twice. **(P)**

### 5.2 The plan and its validation

**FR-PLAN-1.** The **plan** is the list of problems to solve, each with its limits. It is the maintainer's view of what Orchestrarr intends to do. **(M, "the plan that I validate")**
**FR-PLAN-2.** For each problem the plan states: what the problem is, the application and environments concerned, **how far the work may go** (up to production, or stopping at a given world), the budget, and whether the maintainer is to be warned. **(P)**
**FR-PLAN-3.** The maintainer validates **one problem at a time** or a **standing rule**, or both. **(M for both; recorded as a default)**
**FR-PLAN-4.** A **standing rule** describes a class of problems and the limits that apply to it, for example: dependencies reported as deprecated are bumped; majors that break the build are flagged complex; vulnerabilities of high severity go first. A rule applies only inside the perimeter. **(M for the example, P for the form)**
**FR-PLAN-5.** A validation is an **approval by the maintainer** with the same assurance as today (an authenticator code); a standing rule is a **delegation** that the maintainer can revoke at any time. **(P, built on what exists)**
**FR-PLAN-6.** The maintainer can remove a problem from the plan (set it aside); the decision is kept and can be taken back. **(M, built)**
**FR-PLAN-7.** The plan shows, per problem, where it stands: waiting, in a world, waiting for a mandate, closed, failed, set aside. **(P)**
**FR-PLAN-8.** Nothing starts that is not in the plan or covered by a standing rule. **(M, from "no autonomy of decision")**

### 5.3 Missions

**FR-MIS-1.** A **mission** has: an objective (the problem, in words), the application and the repositories concerned, **one world**, a frame, a budget, the inputs (the findings, the history of earlier missions on this application), and the dossier it must hand over. **(M for world and content; P for the details)**
**FR-MIS-2.** A mission is **not a list of steps.** The objective says what must be true at the end, not how to get there. **(M)**
**FR-MIS-3.** A mission's life: **proposed, mandated, running, handing over, accepted, refused, ended** (done, complex, failed, stopped). **(P)**
**FR-MIS-4.** A mission ends in one of: **done**, **done and complex**, **failed cleanly**, **stopped** (kill switch or budget). In every case it leaves a dossier and the system in a known state. **(M for clean stop, P for the names)**
**FR-MIS-5.** The worker agent may **propose a further mission** (for example, "this bump needs a refactor first"). The control plane accepts it only within the plan's limits and a bound on the **number and the depth** of missions for one problem. **(M for the idea, P for the bound)**
**FR-MIS-6.** At most **one mission at a time works on one application**. **(P)**
**FR-MIS-7.** The number of missions running together is bounded by what the machines can carry; in the laboratory, **two staging environments at once**. **(M)**
**FR-MIS-8.** The history of missions per application is kept and is available to later missions (and to the memory product). **(M for memory, P for the form)**

### 5.4 The three worlds

Each world has its own **place, rights and acts**. The rights are given by the environment (credentials, protection rules, network), not by the instructions given to the agent.

**The staging world** **(M)**
**FR-WLD-1.** The workspace is **in the staging environment**: the application is deployed there from the branch being worked on, as a preview environment is used for a front end. The agent changes the code, rebuilds, redeploys, tests, again and again. **(M)**
**FR-WLD-2.** The staging world has **no real secret and no real side effect** (no real order, no real credential, no mail to real people). **(M)**
**FR-WLD-3.** The worker agent can run what a developer runs there: build tools, tests, container tooling, requests, a browser automation. **(M)**
**FR-WLD-4.** At the end of a staging mission the environment is **restored to its baseline** so that the next mission starts clean. **(P)**
**FR-WLD-5.** A staging mission cannot push to the forge or reach production. **(P)**

**The Git world** **(M)**
**FR-WLD-6.** The agent works with the forge: branch, pull request, reading the CI/CD and quality pipelines, fixing what they report, merging into the integration branch when the repository's rules allow. **(M)**
**FR-WLD-7.** The agent **never modifies a pipeline** or the files that decide quality and release (workflows, quality configuration, ownership files, protection rules). If one of them is itself broken, the agent may open a pull request on it but **never merges it** (CASE-08). **(M)**
**FR-WLD-8.** The agent never pushes directly to a protected branch. **(M, rule 5)**
**FR-WLD-9.** The agent may add or change tests when the quality gate needs it (coverage, for example); every change is visible in the pull request. **(M)**
**FR-WLD-10.** The agent's identity on the forge can **only do what the protection rules allow it**; a rule that forbids merging a change to a protected file stops the agent whatever it intends. **(M for the principle, P for the mechanism)**

**The release world** **(M)**
**FR-WLD-11.** The agent deploys to production **through the way the environment is deployed** (GitOps, a declared deployment, an infrastructure-as-code apply), never by an improvised command on a machine. **(P)**
**FR-WLD-12.** After a deployment the agent **verifies** the application as a tester would (section 5.8). **(M)**
**FR-WLD-13.** If the verification fails, the agent **rolls back**; the way back is known before the deployment starts. **(M, rule 5)**
**FR-WLD-14.** A release mandate is issued only for an artifact that passed the Git world's pipelines, **the same one** that was proved in staging when the application has one. **(P)**

### 5.5 Mandates

**FR-MAN-1.** A **mandate** is issued by the control plane for **one world and one mission**. It states: the world and the place, the application and repositories, the **acts allowed**, the frame, the **budget**, the **expiry**, and the dossier expected. **(M for the idea, P for the contents)**
**FR-MAN-2.** The first mandate of a problem is issued from the plan (validated, or covered by a standing rule). The following ones are issued **only after the controller has crossed** the previous mission. **(M)**
**FR-MAN-3.** A mandate **cannot be widened** by the agent that holds it. A change of scope is a new mandate. **(P)**
**FR-MAN-4.** A mandate **expires** and can be **revoked**: by the kill switch, by the maintainer, by the budget. A revoked mandate stops its mission at once. **(M for the stop, P for the rest)**
**FR-MAN-5.** Every mandate, every refusal and every crossing is written in the **audit trail**, with who decided and on what evidence. **(M, rule 5)**
**FR-MAN-6.** A mandate on the code of an application is **refused while the application's staging requirement is not met** (section 5.7). **(M)**
**FR-MAN-7.** Preconditions that the code can check are **checked by the code**, not left to the controller: the plan covers it, the budget is available, the kill switch is not engaged, the staging requirement is met, the previous mission's dossier exists, the continuous integration is green on the exact commit for the release world, the diff touches no forbidden file. **(P)**

### 5.6 The controller

**FR-CTL-1.** The controller is **an agent of the control plane**, separate from the worker agent. **(M)**
**FR-CTL-2.** The controller is **independent**: it starts with its own context, reads **only the dossier and the facts the control plane gives it** (the diff, the pipelines' result, the evidence), and does not read the worker's reasoning. **(M for "without bias", P for the form)**
**FR-CTL-3.** The controller checks that the **contract was respected**: the objective is met, the frame was kept, the evidence supports the claim, nothing unexpected changed. **(M)**
**FR-CTL-4.** Its decision is one of: **grant** the next mandate; **send back** with precise remarks (the worker continues in the same world); **end** the mission; **escalate** to the maintainer. Every decision comes with its reasons. **(P)**
**FR-CTL-5.** The controller **cannot widen the frame**, cannot grant a world that the plan does not allow, and cannot waive a check that the code makes. **(P)**
**FR-CTL-6.** The controller is **bounded** too: a number of send-backs per mission, after which the mission ends and the maintainer is told. **(P)**
**FR-CTL-7.** When the controller and the worker disagree repeatedly, the problem is **escalated** rather than argued indefinitely. **(P)**

### 5.7 Staging readiness

This section answers the maintainer's question: *is there an environment that is functional, that lets us test, and when is a mandate on the code allowed?* **(M)**

**The need.** Whether an application can be tested in staging depends on the application. A one-container tool may need nothing but a blank machine; a multi-service application needs its database, its other services and its front end. **(M)**

**FR-STG-1.** Every application has a **staging requirement**, one of: **none** (it can be tested on a blank machine); **dependencies** (it needs declared other services or data); **full stack** (it needs the whole constellation it runs in). **(P, from the maintainer's examples: a monitoring tool needs nothing; a tentacular application needs its whole stack)**
**FR-STG-2.** Every application that runs in production **must exist in staging** as a prerequisite, switched off or created on demand. Third-party applications are included. **(M)**
**FR-STG-3.** An application has a **staging state**, one of: **not defined**; **defined** (how to bring it up is known); **provisioned** (it exists and can start); **baseline proven** (it was brought up on unchanged code and its tests passed, on a given revision and date); **stale** (the code or the production configuration moved since); **broken**. **(P)**
**FR-STG-4.** A mandate on an application's code is **granted only when its staging requirement is met**: requirement none, or state **baseline proven** and not stale. For an application with no staging requirement defined, the rule of CASE-17 applies. **(M)**
**FR-STG-5.** The **baseline proof is itself a mission** (CASE-20): bring the application up on the unchanged code and test it. Its result is the state above. **(M)**
**FR-STG-6.** Staging is **the same infrastructure code as production, applied to another inventory**, so that the two drift as little as possible. **(M)**
**FR-STG-7.** Staging environments are **created on demand and restored to a clean baseline** after a mission (virtual machine snapshots). **(M, the idea of virtual machines on demand)**
**FR-STG-8.** The staging platform's capacity (two stagings at once; small disk) is a **constraint the scheduler respects**: missions wait for a free staging. **(M)**
**FR-STG-9.** The control plane's rights on the staging platform are **limited to the pool of staging machines**, never the whole host. **(P)**
**FR-STG-10.** Staging holds no real secret and has no real side effect (FR-WLD-2). **(M)**

**What Portarr says and what Orchestrarr says.** Portarr can say **facts**: the staging environment exists, which workloads run in it, whether they are up. Orchestrarr says the **verdict** that only a test can give: baseline proven. Where that verdict is stored, and whether Portarr shows it, is open (section 11). **(P)**

### 5.8 Verification by the agent

**FR-VER-1.** After a change, the agent **verifies the result as a tester would**: real requests, a browser run, the behaviour that matters for that application. A bare health check is used only when nothing better can be done. **(M)**
**FR-VER-2.** The verification leaves **evidence** in the dossier: the requests and answers, captures, the trace of the browser run, the tests run and their results. **(M for evidence of tests, P for the list)**
**FR-VER-3.** The maintainer can read the evidence afterwards, without redoing the verification. **(P)**
**FR-VER-4.** The controller judges whether the evidence supports the claim; it does not accept "it works" without it. **(P)**

### 5.9 The frame and safety

**FR-SAF-1.** The agent never touches: the pipelines and the files that decide quality and release; any secret; data outside the mission's repositories; anything outside the perimeter. **(M)**
**FR-SAF-2.** The frame is **enforced outside the agent**: by the forge's protection rules and code owners, by credentials limited to one repository and one world, by the isolation of the workspace, by network limits. An instruction in a pull request, a comment, a changelog or a repository cannot widen it. **(M for outside enforcement; P for the prompt-injection stance)**
**FR-SAF-3.** What the agent reads while it works (comments, reports, scanner messages, repository content, web pages) is **data and never an instruction**. **(P)**
**FR-SAF-4.** The agent **never holds a production secret**. The acts that need one are done by the control plane or the runner of that world, for the mandate and nothing more. **(M for "does not touch secrets", P for the mechanism)**
**FR-SAF-5.** A **global kill switch** stops every mission and revokes every mandate; unknown state counts as stopped. **(M, built)**
**FR-SAF-6.** **Budgets** bound every mission and every controller decision: time, attempts, the subscription's quota. A mission that reaches its budget **stops cleanly** and reports. A quota that is used up pauses and resumes later; it is not a failure. **(M, "generous budgets")**
**FR-SAF-7.** A complete **audit trail** records every decision, mandate and act, in an order that cannot be rewritten. **(M, built)**
**FR-SAF-8.** A migration of a database is allowed only with a **backup taken first and a way back**; deleting data and handling a secret are never done by the agent alone. **(P, default)**
**FR-SAF-9.** An **exposed secret** is removed from the code and the maintainer is told at once; **the rotation is the maintainer's** (CASE-13). **(M)**
**FR-SAF-10.** The agent's model credential is the maintainer's subscription token, held by the runner, never written to a log, a prompt or a repository. **(M)**

### 5.10 Complexity and notifications

**FR-NOT-1.** The **worker agent decides** that a case is complex and **says why**. **(M)**
**FR-NOT-2.** A complex case, when done, sends the maintainer **an email** that says: what the problem was, what was done, why it is complex, **what to test** in the application, and a link to the dossier. **(M)**
**FR-NOT-3.** The maintainer is also notified **at once** of: a serious vulnerability, an exposed secret, a mission that failed or was stopped, a loop that hit its limit, a staging that is broken, the kill switch. **(M for the first two, P for the rest)**
**FR-NOT-4.** A simple success sends **nothing** by default; it appears in the history. **(M, "I touch nothing")**
**FR-NOT-5.** The notifier is a channel of the product (mail), not a screen. The path (through Portarr or direct) is open. **(M for mail, path open)**

### 5.11 Failure, rollback and loops

**FR-FAIL-1.** A mission that cannot be finished within its budget **stops cleanly**: nothing half-merged, nothing half-deployed, the branch and the notes kept, a report of what was tried (CASE-14). **(M)**
**FR-FAIL-2.** A problem found **after** a world (a staging test fails, the pipelines fail, production misbehaves) opens a **new mandate** to repair it, in the world where it can be repaired. **(M)**
**FR-FAIL-3.** In production, a failing verification triggers the **rollback first**, then a repair mandate. **(M for rollback, P for the order)**
**FR-FAIL-4.** The loop is **bounded** per problem: a maximum number of mandates and a maximum time. When reached, the work stops and the maintainer is told. **(M)**
**FR-FAIL-5.** A mission's failure is **never hidden**: it is in the history, the report and, when it matters, a notification. **(M, rule 5)**
**FR-FAIL-6.** A failed problem can be **taken again** by the maintainer with a new validation; it is not retried forever by itself. **(P)**

### 5.12 What the maintainer sees

The maintainer's needs, not a screen design. The existing screens are adapted; no new screen is added unless the maintainer asks. **(M, rule 16 of CLAUDE.md)**

**FR-VIEW-1.** The **plan**: every problem, where it stands, how far it may go, its priority, in one table the maintainer already knows. **(M)**
**FR-VIEW-2.** The **missions in progress**, live: the world, what the agent is doing, its trace, its evidence as it appears. **(M, "live by default")**
**FR-VIEW-3.** The **history** of what was done and decided, with the dossiers and reports. **(M)**
**FR-VIEW-4.** The **state of staging** per application: requirement, state, last baseline, availability. **(P)**
**FR-VIEW-5.** **Notices** (warnings, repositories that cannot be read, a broken staging) in the notification bell, never in banners. **(M)**
**FR-VIEW-6.** The **global stop** and the **audit trail**. **(M, built)**

---

## 6. The four reference cases, traced

These are traces of behaviour, not scripts: the steps inside a world are the agent's.

**CASE-01: a routine dependency bump.**
The bot opens a pull request; the problem is in the plan (a standing rule covers it). If the application has a staging, its baseline is checked, a **staging mission** deploys the bump there and the agent verifies the application; the controller reads the dossier and grants the Git mandate; the agent follows the pipelines and merges into develop; the controller grants the release mandate; the agent deploys and verifies. Closed. The maintainer received nothing.

**CASE-02: a dependency is deprecated, the code must change.**
The finding (a deprecation) opens a problem. The **staging mission** is the heavy one: the agent reads the library's notes and the build's errors, finds the replacement, refactors, writes or adapts tests, runs them, tests the application in staging. Its dossier says what changed and why. It marks the case **complex**. The Git and release worlds go as in CASE-01. At the end, an email tells the maintainer what to test.

**CASE-03: a security advisory (Log4j-like).**
The finding has a high severity: the problem goes **first in the queue** and the maintainer is notified **at once**. If a fixed version is a drop-in, it goes like CASE-01; if the fix changes an API, like CASE-02. If no fixed version exists, the agent looks for another way to remove the exposure and, failing that, the controller **escalates** to the maintainer with the facts.

**CASE-04: a container image has a newer version.**
The finding names an image behind. If a tool such as a version-update bot already raised a pull request, it is CASE-01. Otherwise the agent finds where the version is written (compose, a manifest, a Terraform file), and in the staging world deploys the new version, **with the application's whole constellation** where the requirement says so, and verifies it. The Git world changes the version in the repository that owns it; the release world deploys it through the way the environment is deployed, verifies, and rolls back if it does not answer.

---

## 7. What is expected from Portarr

Portarr sees; Orchestrarr acts (the constellation). This is what the cases need Portarr to see. It is a need, not a design of Portarr, which the maintainer builds in its own workspace. **(M for the idea; the shapes are P)**

| # | Need | Why | Cases |
|---|---|---|---|
| PN-1 | **Vulnerability findings** for dependencies, images and containers (a scanner such as Trivy, and the alerts of the forge) | The cases that start from a vulnerability | 03, 13 |
| PN-2 | **Version findings** for images in compose files, manifests and infrastructure code, with where each version is written | A bot that reads only dependencies misses them | 04, 05 |
| PN-3 | **End of support** of operating systems, platforms and runtimes (a public database of release cycles), per machine and per node | The maintainer learned by chance that his backend's operating system had been out of support for months | 11 |
| PN-4 | **Platform versions**: the K3s version of each node and the operating system of each machine | Same | 11 |
| PN-5 | **Deprecation findings**: signals that a library or an API is deprecated | The only way a deprecation exists without a failing build | 02, 16 |
| PN-6 | **The staging environment as an environment of its own**, with what runs in it and whether it is up | A mandate on the code depends on it | 20, 19 |
| PN-7 | **A staging counterpart per application**: does it exist, which workloads, which dependencies, how they are linked | The staging requirement and the "do we have everything?" question | 19, 20 |
| PN-8 | **A parity gap finding**: an application that runs in production and has no staging counterpart | The prerequisite of the staging policy | 19, 20 |
| PN-9 | **The dependency map between applications** (which service needs which) | To bring up "the constellation" | 19 |
| PN-10 | **How each application is deployed** (GitOps, compose, an infrastructure-as-code apply) and where its version is written | The release world deploys the way the environment is deployed | 04, release |
| PN-11 | **Health facts** after a deployment (an application answers, a check from Portarr) | A fact the controller can read next to the agent's own evidence | 06 |

Open: where the **baseline verdict** lives (FR-STG-3) and whether Portarr displays it (section 11). A brief for the Portarr workspace is kept privately, with the real names.

---

## 8. The laboratory

**FR-LAB-1.** The laboratory is **a set of deliberately broken projects**, one or more per case of [USE-CASES.md](USE-CASES.md), with the staging machine where they run. **(M)**
**FR-LAB-2.** A case can be **reproduced on purpose**: plant the fault, let it be seen, validate the plan, watch what happens, live. **(M)**
**FR-LAB-3.** A case can be **replayed**: the laboratory is **restored to its faulty state** between runs. **(P)**
**FR-LAB-4.** Each case has an **observable pass criterion** (the application is fixed and verified in staging, the pull request merged, the rollback happened, the notification was sent, the frame was refused). **(P)**
**FR-LAB-5.** The cases are the **end-to-end tests** of the product: a case that does not resolve is a defect of the product. **(M)**
**FR-LAB-6.** A **real incident** that Orchestrarr mishandles becomes a new case. **(M)**
**FR-LAB-7.** The laboratory contains **negative cases**: attempts to make the agent step outside the frame, to modify a pipeline, to read a secret; they must be **refused by the environment**. **(M)**
**FR-LAB-8.** The laboratory contains **no real secret and no real side effect.** **(M)**

---

## 9. Qualities the product must have

- **Plastic.** A new kind of problem needs a new case and perhaps a new source of findings, not a new workflow in code. **(M, "the runner must be plastic")**
- **Autonomous**, with the maintainer's validation as the only routine human step. **(M)**
- **Observable.** Everything that happens is visible live and kept in the history. **(M)**
- **Bounded.** Every mission, loop and controller decision has a limit. **(M)**
- **Safe by construction.** The frame holds even if an agent misbehaves. **(M)**
- **Light on the subscription.** The agent uses the maintainer's subscription: budgets in attempts, turns and minutes, and a pause when the quota is used up. **(M)**
- **Replayable.** The laboratory can reproduce any case. **(M)**

---

## 10. What happens to what exists

At the functional level only. The architecture will say where each piece goes.

- **Kept**: reading the findings and the pull requests; the description of the perimeter; the plan as a table; approval with an authenticator code and standing delegations; the audit trail; the kill switch; the history; the notification bell; the live screens; the isolation of the runner.
- **Replaced**: the three fixed tasks (validate, repair, merge) become **missions in worlds with mandates**; the agent gets a shell and the tools of a developer; the controller is new.
- **Dropped**: the rules in code that decide, row by row, how far a problem goes; the validation profile written by hand for each repository; the dry-run merge as the end of the chain.
- **New**: the controller; the mandates; the staging readiness (profile, state, baseline); the staging platform's control; the notification by mail; the laboratory.

---

## 11. Decisions and open points

**Decided by the maintainer**
- Missions defined by place, as worlds (staging, Git, release), moved between by mandates; the controller is an agent; a bounded loop. (2026-11-13)
- The agent is a developer with a mission, and the frame, not the path, is fixed. (2026-11-12, 2026-11-13)
- A simple case is finished in production once staging is verified; "complex" is decided by the agent and notified by email; a broken pipeline is fixed by a pull request that the maintainer merges; an application with no staging or tests goes on like the others. (2026-11-13)
- Verification is done by the agent as a tester; a bare health check is a last resort. (2026-11-13)
- Every production application must have a staging counterpart; staging mirrors production by the same infrastructure code; no real secret or side effect in staging. (2026-11-13)
- Staging runs on the former hypervisor: two virtual machines at once, a small disk. (2026-11-13)

**Open points**
1. How the **staging requirement** of each application is declared (in the description of the perimeter, in Portarr, or in the repository), and who writes it.
2. Where the **baseline verdict** (staging state) is stored and whether Portarr shows it.
3. What the **controller** checks by judgement and what the code checks mechanically, and how it is kept independent.
4. What a **mandate** contains exactly (acts, expiry, budget) and how it relates to today's approvals.
5. The **bound** on missions, send-backs and loops per problem.
6. How a **deprecation** without a failing build becomes a finding.
7. How a **deployment** on a machine without GitOps is done (a declared action through a configured connection is the default).
8. **How the notification reaches the maintainer** (through Portarr or directly).
9. The shape of the **laboratory** and how the staging is restored between runs.
10. How the product proves that the **staging is the same code** as production.
