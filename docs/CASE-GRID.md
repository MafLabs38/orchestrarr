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
| 04 Newer image | Row shown, **not selectable** | Old images in `edge-stack` | no | open |
| 05 Floating tag | Row shown, **not selectable** | `latest` in `edge-stack` | no | open |
| 06 Green in CI, broken at run time | Failure after deploy goes back to staging | `ledger-worker` Python bump (fails at the check after deploy) | no | **manual**, in part: the loop returns to staging; no revert or fix proven |
| 07 Many pull requests at once | Rows one by one, no order, no grouping | Several bumps on `shop-api` | no | open |
| 08 CI broken, no pull request | Row shown, **not selectable** | `pipeline-scenarios` (seven breakages) | no | open; the output is a patch or an issue, never a push |
| 09 Obsolete base image | Row shown, **not selectable** | `node:14`, `python:3.7` | no | open (a base-image pull request is only a bump) |
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

**Ticked: 4 of 21** (01, 02, 03, 16): CASE-03, CASE-03b and CASE-16 passed three times in a row on the code of 2026-11-23 (the replays were played again after each change of the lead's bounds and of the controller's rules). Played by hand in whole or in part: 06, 14, 15, 21.

## What the grid shows about the structure

- **Only the pull request and the dependency alert are actionable.** The other rows that are not a pull request are created with `Ceiling.NoAction` ([DecisionTable.cs](../src/Orchestrarr.Core/Decisions/DecisionTable.cs)): exposed secrets, failing CI, outdated and floating images, dormant repositories can be read and cannot be ticked. A dependency alert is ticked as it is and solved by a free agent in staging (ADR 0048); cases 04, 05, 08, 09 and 13 need the same path for their rows.
- **The repair policy is narrower than the cases, and a problem with no pull request does not use it.** The repair agent may not touch tests, dependencies, the build or the pipelines (ADR 0044); a problem that has no pull request is worked by a free agent, whose change the runner reads and `ChangePolicy` bounds (pipelines, secrets and agent files never; dependencies, tests and Dockerfiles yes, ADR 0048). Cases 04, 05 and 09 (a Dockerfile or a compose file) go through the same path.
- **The sandbox is replayable now.** `develop` cannot be force-pushed (the repository rules are the same as a real project's, and stay), so a reset restores the seed tree **through a pull request**, and the faults are planted by the lab itself, not waited for from a bot. A bot's pull requests are never closed by the lab (a closed one is not opened again), and the bot opens new ones when `develop` goes back: a scenario names its own pull request by what it planted.
- **What the first replays found.** The table keeps what it read (the reading of the forge is kept 5 minutes, `GitHubOptions.CacheSeconds`, and the work-item catalog 30 seconds), so after a reset it still offered closed pull requests, and the merge was refused on them (405, 409): the runner now asks for a fresh reading and waits until the table shows the pull requests it planted. Nothing was merged on a closed pull request (the forge refused), so the failure is safe, but a row that cannot be acted on is offered until the next fresh reading, up to five minutes: to settle with the cases that follow (07, many pull requests at once, will meet it harder).
- **What CASE-02 found.** The objective of the Git and release worlds named the original pull request, so the controller refused the merge of the repair that replaces it: the objectives now say that a repair replaces the pull request. And a major bump of a runtime library can need a newer runtime than the image of the seed (Express 5 on `node:14`): the planted change carries the base image that its author would bump, because the repair agent does not touch build or container files; an obsolete image on its own is case 09.
- **A problem that is not a pull request** has no description in the sentence the controller reads as the control plane's own, on purpose; its description now reaches the lead agent as untrusted data with its source (ADR 0046). The alert row can be started since lot 1 (ADR 0048), with the findings of the source given to the lead beside the description, as data.
- **A seed whose pipeline is red on purpose** (the pipelines of case 08) cannot be restored through a pull request that must pass its checks: the required check is a global setting today and becomes one per repository with that case (lot 3). The reset of a repository that is already at its seed does not need it.
- **A tick is one verdict of a model** (the controller): it is not deterministic. A case is ticked when its scenario passed three times in a row since the last change to the code it runs through; CASE-01 passed six replays in a row after its last change, and CASE-02 three (2026-11-22).
- **What CASE-03 and CASE-16 found.** (1) The free agent's output can be larger than one call brings back (a lock file): the runner reads each file in slices, the first run lost the tree and the handover said the change could not be carried. (2) The controller sent back a handover that only *told* what changed: the dossier now carries the lines that the runner read in the repository. (3) A count that is larger than the findings (the fake Portarr said six alerts, the source named two) made the lead promise "all six" and the controller ask for the four it was never given: the sample now says two, and both prompts say that the findings are what the control plane knows. (4) The advisory database moved under the scenario (lodash 4.17.21 is itself flagged now): the agent found it and the controller escalated instead of granting, which is the behaviour wanted; the scenario names the version that fixes it. (5) A library replaced by one the production image cannot run (`util.parseArgs` on the `node:14` image) passed staging and failed the release, and the loop went back to staging: the case that the gap between the staging and the production runtimes (case 20) will have to prevent. (6) The same row is the same problem: what a previous run made (a repair, its pull request) was taken for the new run's and the staging proof fetched a merged pull request; a start now forgets it. (7) The table said *No Runner* while the runner was busy in a long mission or a reset: any call of a known runner now counts as a sign of life. (8) The lead's instruction, now richer, was refused by the bound of 800 characters in about one run in five (the audit trail said so: *the instruction of the Staging world is not short plain text*): the bound is 2000 and the lead is told it.
- **Not yet built at all**: grouping and ordering (07), the report and the mail (14 and the complex marking), the cases that need more lab (11, 12, 18, 19).

## Order of work

The unit of work is a lot of cases that share a mechanism, one pull request at a time (CLAUDE.md rule 18), the grid updated in each:

0. **The bench**: reset, planting, the scenario runner, this grid. Nothing below can be ticked without it.
1. **Dependencies**: 01, 02, 03, 16 are ticked; 07 (many pull requests at once: grouping, order, one deployment) is the next pull request of the lot.
2. **Images and run time**: 04, 05, 06, 09, 17, 20.
3. **Pipelines and secrets**: 08, 13, 15, 21.
4. **Failure and telling the maintainer**: 14, the complex marking, the mail, the global stop.
5. **The complex cases**: 10, 11, 12, 18, 19.

Then the fake Portarr is replaced by the real one.
