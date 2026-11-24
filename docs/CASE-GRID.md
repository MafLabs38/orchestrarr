# The case grid: what decides that the work is finished

> **Status**: v0.1 (2026-11-22). This grid is the definition of done of the product, set by the maintainer: the work is finished when every case of [USE-CASES.md](USE-CASES.md) is **ticked for real** (D-99, [ADR 0045](adr/0045-a-case-is-ticked-by-a-replayable-scenario.md)). It is updated by every pull request that moves a case. Where a status is not verified, it says so.

## What "ticked" means

A case is ticked only when its **scenario** runs without anyone, from a sandbox reset to the final state:

1. the sandbox is reset to its seed (`lab/sandbox.sh reset`, see [lab/README.md](../lab/README.md));
2. the fault of the case is planted (`lab/sandbox.sh plant <case>`);
3. the fake Portarr lists the problem and the interface is driven the way the maintainer drives it (sign in, tick the row, validate);
4. the final state is checked by a program, not by eye: what the forge says (the pull request merged or closed, the file changed) and what the missions say; the health of the deployed application is read by the release world's own verification, and an independent read of it, of the mail and of the audit trail is added with the cases that need them;
5. the sandbox is reset again.

A demonstration played once by hand proves that a path exists, not that the case is handled; it is marked *manual* below and does not tick the case. A case that cannot resolve is a defect of the product (USE-CASES §4).

## The grid

| Case | What the table does today | Planted in the sandbox | Replayable scenario | Status |
|---|---|---|---|---|
| 01 Routine bump | Row bound to a pull request, selectable | `lab/scenarios/CASE-01.json`: a lodash patch bump on `shop-api` | **yes** (`lab/e2e/run-scenario.cjs`) | **ticked** (2026-11-22): reset, plant, sign in, refresh, tick, validate; staging, controller, merge, release; the pull request is read as merged on the forge; reset again |
| 02 Deprecated API, code must change | A failing pull request goes to *Repair* | `lab/scenarios/CASE-02.json`: Express 5 and the base image it needs, on `shop-api` | **yes** | **ticked** (2026-11-22): the staging fails, the agent repairs the route, the gateway opens the repair pull request and merges it, the original is closed and the release answers |
| 03 Security advisory | An alert row (no pull request) is selectable; the lead plans from the findings, a free agent in staging makes the change, the gateway opens the pull request on the default branch | `lab/scenarios/CASE-03.json` (lodash and minimist, each with a fixed version) and `CASE-03b.json` (lodash with no fixed version: the exposure is removed another way) on `shop-api`, the findings written to the fake source (`LAB_FINDINGS_FILE`) | **yes** (both) | **ticked** (2026-11-22, see the note below the table) |
| 04 Newer image | An image row (no pull request) is selectable; the lead is told where the image is written and plans (a major one is complex), a free agent in staging makes the change, the gateway opens the pull request (ADR 0051) | Old images in `edge-stack`; no fault is planted, the seed is the fault | **yes** (`lab/scenarios/CASE-04.json`: the web image) | **ticked** (2026-11-24, three replays in a row on the final code), **up to the merge**: a compose stack is not deployed by the release runner, so its grant stops at the Git world (`Missions:SandboxWorldLimits`) and the deployment and the check after it are not played |
| 05 Floating tag | Same path as 04: a floating tag is a row that can be ticked | `latest` in `edge-stack` (`hashicorp/http-echo`) | **yes** (`lab/scenarios/CASE-05.json`) | **ticked** (2026-11-24, three replays in a row on the final code), up to the merge for the same reason as 04; the agent pins a version and touches nothing else |
| 06 Green in CI, broken at run time | Failure after deploy goes back to staging | `ledger-worker` Python bump (fails at the check after deploy) | no | **manual**, in part: the loop returns to staging; no revert or fix proven |
| 07 Many pull requests at once | One problem at a time on a repository: the others wait in line, planned and not started; a pull request that conflicts with its base, or that moved, is ended at once and the line goes on (ADR 0049) | `lab/scenarios/CASE-07.json`: three bumps on `shop-api` (lodash, express, mocha), two of them on neighbouring lines of `package.json`; in a second round the lab plays the dependency bot (it makes the open one again on the new base) and the pull request is ticked again | **yes** (two rounds) | **ticked** (2026-11-24): the first is merged and released, the second conflicts with the new base and is ended in seconds, the third is merged and released; once the bot has rebased the second it is merged and released; no grouping and no single deployment (not what the maintainer asked) |
| 08 CI broken, no pull request | Row shown, **not selectable** | `pipeline-scenarios` (seven breakages) | no | open; the output is a patch or an issue, never a push |
| 09 Obsolete base image | Same path as 04; a Dockerfile is a place where an image is written (`kind: dockerfile` of the description) | `python:3.7` in the `ledger-worker` Dockerfile | **yes** (`lab/scenarios/CASE-09.json`) | **ticked** (2026-11-24, three replays in a row on the final code): the base is raised to a supported Python (3.13 in the replays), the tests pass on it, the pull request is merged and the application is redeployed and checked |
| 10 Major framework upgrade | none | none | no | open |
| 11 Platform upgrade | none | none; needs a staging cluster | no | open |
| 12 Database major | none | none; needs a database with data | no | open |
| 13 Exposed secret | Row shown, **not selectable** | none | no | open |
| 14 The agent cannot solve it | Loops are bounded (ADR 0036); no report, no mail | `ledger-worker` pinned versions | no | **manual**, in part |
| 15 Pushed outside the frame | Path policy of the repair agent, a workflow change refused by the controller | partly | no | **manual**, in part; the variants of the list are not played |
| 16 Abandoned dependency | Same path as 03; the finding names a replacement, the lead marks the problem complex | `lab/scenarios/CASE-16.json`: `minimist` abandoned (replace it and migrate the calls) with an advisory on `lodash`, on `shop-api` | **yes** | **ticked** (2026-11-22): the dependency is gone, the lead marked it complex (the mark is a line of the Missions screen; the mail is lot 4) |
| 17 No tests, no staging | none | none | no | open (SPEC Q-38) |
| 18 Backup does not work | none | none | no | open |
| 19 Several services | none | none | no | open |
| 20 Staging proved healthy first | not verified | none | no | open, not verified |
| 21 GitOps and write credentials | a workflow change is refused by the controller | none | no | **manual**, in part |

**Ticked: 8 of 21** (01, 02, 03, 04, 05, 07, 09, 16): CASE-03, CASE-03b and CASE-16 passed three times in a row on the code of 2026-11-23 (the replays were played again after each change of the lead's bounds and of the controller's rules), CASE-07 three times in a row on the code of 2026-11-24, and CASE-04, CASE-05 and CASE-09 three times in a row on the code of 2026-11-24 (the images, ADR 0051). Played by hand in whole or in part: 06, 14, 15, 21.

## What the grid shows about the structure

- **Only the pull request, the dependency alert and the image rows are actionable (a problem with no pull request is solved by a free agent, ADR 0048 and 0051).** The other rows that are not a pull request are created with `Ceiling.NoAction` ([DecisionTable.cs](../src/Orchestrarr.Core/Decisions/DecisionTable.cs)): exposed secrets, failing CI, dormant repositories, and an image that the platform keeps or that is not written in the code that was read, can be read and cannot be ticked. A dependency alert, and an image written in the code of its repository, is ticked as it is and solved by a free agent in staging that pushes its own branch (ADR 0048, 0051); cases 08 and 13 need the same path for their rows.
- **The repair policy is narrower than the cases, and a problem with no pull request does not use it.** The repair agent may not touch tests, dependencies, the build or the pipelines (ADR 0044); a problem that has no pull request is worked by a free agent, whose pushed commit the control plane reads on the forge and `ChangePolicy` bounds (pipelines, secrets and agent files never; dependencies, tests and Dockerfiles yes, ADR 0048). Cases 04, 05 and 09 (a Dockerfile or a compose file) go through the same path.
- **The sandbox is replayable now.** `develop` cannot be force-pushed (the repository rules are the same as a real project's, and stay), so a reset restores the seed tree **through a pull request**, and the faults are planted by the lab itself, not waited for from a bot. A bot's pull requests are never closed by the lab (a closed one is not opened again), and the bot opens new ones when `develop` goes back: a scenario names its own pull request by what it planted.
- **What the first replays found.** The table keeps what it read (the reading of the forge is kept 5 minutes, `GitHubOptions.CacheSeconds`, and the work-item catalog 30 seconds), so after a reset it still offered closed pull requests, and the merge was refused on them (405, 409): the runner now asks for a fresh reading and waits until the table shows the pull requests it planted. Nothing was merged on a closed pull request (the forge refused), so the failure is safe, but a row that cannot be acted on is offered until the next fresh reading, up to five minutes: to settle with the cases that follow (07, many pull requests at once, met it: the line starts a problem only when its repository is free).
- **What CASE-02 found.** The objective of the Git and release worlds named the original pull request, so the controller refused the merge of the repair that replaces it: the objectives now say that a repair replaces the pull request. And a major bump of a runtime library can need a newer runtime than the image of the seed (Express 5 on `node:14`): the planted change carries the base image that its author would bump, because the repair agent does not touch build or container files; an obsolete image on its own is case 09.
- **A problem that is not a pull request** has no description in the sentence the controller reads as the control plane's own, on purpose; its description now reaches the lead agent as untrusted data with its source (ADR 0046). The alert row can be started since lot 1 (ADR 0048), with the findings of the source given to the lead beside the description, as data.
- **A seed whose pipeline is red on purpose** (the pipelines of case 08) cannot be restored through a pull request that must pass its checks: the required check is a global setting today and becomes one per repository with that case (lot 3). The reset of a repository that is already at its seed does not need it.
- **A tick is one verdict of a model** (the controller): it is not deterministic. A case is ticked when its scenario passed three times in a row since the last change to the code it runs through; CASE-01 passed six replays in a row after its last change, and CASE-02 three (2026-11-22).
- **What CASE-03 and CASE-16 found.** (1) The first design carried the agent's files out of the machine through the runner, which read them back and wrote a diff: a lock file was larger than one call brings back, the controller sent back a dossier that only *told* what changed, and the maintainer named the defect (2026-11-23): **a developer commits and pushes, and the commit on the forge is what is audited**. The agent now pushes its own branch; the control plane reads that commit on the forge and gives the controller its own reading; the runner reads no file. (2) A count that is larger than the findings (the fake Portarr said six alerts, the source named two) made the lead promise "all six" and the controller ask for the four it was never given: the sample now says two, and both prompts say that the findings are what the control plane knows. (3) The advisory database moved under the scenario (lodash 4.17.21 is itself flagged now): the agent found it and the controller escalated instead of granting, which is the behaviour wanted; the scenario names the version that fixes it. (4) A library replaced by one the production image cannot run (`util.parseArgs` on the `node:14` image) passed staging and failed the release, and the loop went back to staging: the case that the gap between the staging and the production runtimes (case 20) will have to prevent. (5) The same row is the same problem: what a previous run made (a repair, its pull request) was taken for the new run's and the staging proof fetched a merged pull request; a start now forgets it. (6) The table said *No Runner* while the runner was busy in a long mission or a reset: any call of a known runner now counts as a sign of life. (7) The lead's instruction, now richer, was refused by the bound of 800 characters in about one run in five (the audit trail said so: *the instruction of the Staging world is not short plain text*): the bound is 2000 and the lead is told it.
- **Not yet built at all**: an agent of the Git world that resolves a conflict (the rules of ADR 0049 are meant to keep it from arising), the report and the mail (14 and the complex marking), the cases that need more lab (11, 12, 18, 19).

## What the 21 cases do not prove

The grid says that each **situation** works end to end on the test bed. It does not say that the product is finished (the maintainer, 2026-11-24). What is left outside it, so that it is not mistaken for done:

| Requirement area ([FUNCTIONAL.md](FUNCTIONAL.md)) | What is proved | What is not |
|---|---|---|
| 5.9 The frame and safety | Unit tests of the powers, the mandates, the audit of a commit; case 15 in part | An end-to-end replay of an agent that tries to leave its frame (a secret, a pipeline, an injection in a package) |
| 5.10 Complexity and notifications | The lead marks a problem complex | The mail to the maintainer (cases 03 and 14) and the bell notice of a line that left without starting |
| 5.11 Failure, repair and loops | The loop returns to staging at a failed release, with a raised priority (ADR 0050); the budget stops it | The way back of a release that reached production, and the case 06 and 14 scenarios |
| 5.12 What the maintainer sees | Walked by hand in a real browser at each vertical | A scenario of the Missions, plan and history screens |
| The global stop, the budgets, the restart | The kill switch is used by the lab to end a problem | A scenario of the stop (lot 5), a restart of the control plane in the middle of a problem (the lines and the leases are in memory on the test bed) |
| 7 Portarr | A fake source of findings | The real Portarr, and a real deployment (the forge, the delivery, the production) |
| 10 What exists | | The earlier typed-task path is still in the code (ADR 0038) |

These are the lots after the cases; they are added to this grid as they are played.

## Order of work

The unit of work is a lot of cases that share a mechanism, one pull request at a time (CLAUDE.md rule 18), the grid updated in each:

0. **The bench**: reset, planting, the scenario runner, this grid. Nothing below can be ticked without it.
1. **Dependencies**: 01, 02, 03, 07, 16 are ticked (07 is done as the maintainer's Git flow rule of one work site per repository, not as a batch: ADR 0049).
2. **Images**: 04, 05, 09 are ticked (ADR 0051); 04 and 05 stop at the merge.
3. **Run time**: 06, 17, 20 (the images, 04, 05 and 09, are done).
4. **Pipelines and secrets**: 08, 13, 15, 21.
5. **Failure and telling the maintainer**: 14, the complex marking, the mail, the global stop.
6. **The complex cases**: 10, 11, 12, 18, 19.

Then the fake Portarr is replaced by the real one.
