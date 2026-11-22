# The case grid: what decides that the work is finished

> **Status**: v0.1 (2026-11-22). This grid is the definition of done of the product, set by the maintainer: the work is finished when every case of [USE-CASES.md](USE-CASES.md) is **ticked for real** (D-99, [ADR 0045](adr/0045-a-case-is-ticked-by-a-replayable-scenario.md)). It is updated by every pull request that moves a case. Where a status is not verified, it says so.

## What "ticked" means

A case is ticked only when its **scenario** runs without anyone, from a sandbox reset to the final state:

1. the sandbox is reset to its seed (`lab/sandbox.sh reset`, see [lab/README.md](../lab/README.md));
2. the fault of the case is planted (`lab/sandbox.sh plant <case>`);
3. the fake Portarr lists the problem and the interface is driven the way the maintainer drives it (sign in, tick the row, validate);
4. the final state is checked by a program (the pull request merged or closed, the file changed, the deployment answering, the mail sent, the audit trail), not by eye;
5. the sandbox is reset again.

A demonstration played once by hand proves that a path exists, not that the case is handled; it is marked *manual* below and does not tick the case. A case that cannot resolve is a defect of the product (USE-CASES §4).

## The grid

| Case | What the table does today | Planted in the sandbox | Replayable scenario | Status |
|---|---|---|---|---|
| 01 Routine bump | Row bound to a pull request, selectable | `lab/scenarios/CASE-01.json`: a lodash patch bump on `shop-api` | **yes** (`lab/e2e/run-scenario.cjs`) | **ticked** (2026-11-22): reset, plant, sign in, refresh, tick, validate; staging, controller, merge, release; the pull request is read as merged on the forge; reset again |
| 02 Deprecated API, code must change | A failing pull request goes to *Repair* | Express 5 bump (`shop-api`) | no | **manual**: repaired once by the worker agent, carried into a pull request, merged |
| 03 Security advisory | Row shown, **not selectable** (`NoAction`) | Vulnerable pins exist; no case with and without a fixed version | no | open |
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
| 16 Abandoned dependency | none | none | no | open |
| 17 No tests, no staging | none | none | no | open (SPEC Q-38) |
| 18 Backup does not work | none | none | no | open |
| 19 Several services | none | none | no | open |
| 20 Staging proved healthy first | not verified | none | no | open, not verified |
| 21 GitOps and write credentials | a workflow change is refused by the controller | none | no | **manual**, in part |

**Ticked: 1 of 21** (01). Played by hand in whole or in part: 02, 06, 14, 15, 21.

## What the grid shows about the structure

- **Only the pull request is actionable.** Every row that is not a pull request is created with `Ceiling.NoAction` ([DecisionTable.cs](../src/Orchestrarr.Core/Decisions/DecisionTable.cs)): alerts, exposed secrets, failing CI, outdated and floating images, dormant repositories can be read and cannot be ticked. Cases 03, 04, 05, 08, 09 and 13 need a path from such a row to a mission.
- **The repair policy is narrower than the cases.** The worker agent may not touch tests, dependencies, the build or the pipelines (ADR 0044). Cases 03, 04, 05 and 09 are changes to a dependency, a Dockerfile or a compose file. The policy has to be widened case by case, with the environment still enforcing the frame (ADR 0035).
- **The sandbox is replayable now.** `develop` cannot be force-pushed (the repository rules are the same as a real project's, and stay), so a reset restores the seed tree **through a pull request**, and the faults are planted by the lab itself, not waited for from a bot. A bot's pull requests are never closed by the lab (a closed one is not opened again), and the bot opens new ones when `develop` goes back: a scenario names its own pull request by what it planted.
- **What the first replays found.** The table keeps what it read for five minutes, so after a reset it still offered closed pull requests, and the merge was refused on them (405, 409): the runner now asks for a fresh reading and waits until the table shows the pull requests it planted. Nothing was merged on a closed pull request (the forge refused), so the failure is safe, but a row that cannot be acted on is offered for up to five minutes: to settle with the cases that follow (07, many pull requests at once, will meet it harder).
- **Not yet built at all**: grouping and ordering (07), the report and the mail (14 and the complex marking), the cases that need more lab (11, 12, 18, 19).

## Order of work

The unit of work is a lot of cases that share a mechanism, one pull request at a time (CLAUDE.md rule 18), the grid updated in each:

0. **The bench**: reset, planting, the scenario runner, this grid. Nothing below can be ticked without it.
1. **Dependencies**: 01, 02, 03, 07, 16.
2. **Images and run time**: 04, 05, 06, 09, 17, 20.
3. **Pipelines and secrets**: 08, 13, 15, 21.
4. **Failure and telling the maintainer**: 14, the complex marking, the mail, the global stop.
5. **The complex cases**: 10, 11, 12, 18, 19.

Then the fake Portarr is replaced by the real one.
