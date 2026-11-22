# 0045 — A case is ticked by a replayable scenario, and the test bed is restored through a pull request

- **Status**: proposed (accepted when the pull request that carries it is merged)
- **Date**: 2026-11-22
- **Spec**: D-99 ([SPEC.md](../SPEC.md)). Builds on [USE-CASES.md](../USE-CASES.md) (the cases are the end-to-end tests) and [ADR 0042](0042-a-runner-serves-one-world-and-the-test-bed-validates-through-the-plan.md).

## Context

The first verticals were cut around one demonstration (a bot's dependency bump), and the other families of problems were left to the end. Looking at the 21 cases of USE-CASES, two were proven, by hand, once. **The maintainer decided in the chat of 2026-11-22** that the table of the cases is what says whether the work is finished: the fake Portarr must reproduce every case, and every box must be ticked for real. He does not want to test or to be asked along the way; he is consulted for a major architecture change, to validate a pull request and for a serious finding of the security agent.

Two things made a case impossible to replay: the pull requests that a bot opens are consumed by a run (and a closed one is never opened again), and the repositories of the test bed follow the rules of a real project (a pull request is required, no force push), so `develop` cannot be put back by rewriting it.

## Decision

1. **The grid is the definition of done.** [CASE-GRID.md](../CASE-GRID.md) lists the 21 cases with their real status; every pull request that moves a case updates it.
2. **A case is ticked only by a scenario that runs without anyone**: reset the test bed to its seed, plant the fault, drive the interface as the maintainer does, and read the final state **on the forge** (not in the interface), then reset again. A demonstration played once by hand is marked *manual* and ticks nothing.
3. **A repository of the test bed has a seed**, a commit pinned in the repository of the product (`lab/seeds.json`) and tagged `lab-seed` on the forge; a tag that moved is refused. A reset closes the pull requests of the lab and of the agents (never a bot's: a closed one is not opened again), deletes the work branches, and restores the seed tree **through a pull request that passes the repository's checks** and is squash-merged on the exact head that passed. The rules of the test bed are never changed or bypassed.
4. **The lab plants its own faults** (a branch and a pull request from a scenario file) instead of waiting for a bot's, and a scenario names its pull request by what it planted.
5. **The lab is a development tool, not part of the product**: it holds no power of the product, writes only to the organisation it is given (the test bed: the operator checks it) and to repositories whose seed is pinned, and takes its organisation, account and token from the environment, never from the repository. It does not reproduce the product's pipelines.

## Consequences

- The work is organised in lots of cases that share a mechanism, one pull request at a time; the order is in the grid.
- A scenario can find a defect of the product (the first replays found that the table offers, for up to five minutes, pull requests closed since): it is written in the grid and fixed with the case that needs it.
- The runner needs a live test bed and runs by hand, not in CI.
- The replays found, besides the stale rows of the table, a defect of the controller: it received the whole problem in every world and sometimes asked the Git world for the health of the release, which only the release world produces, and refused a good merge. Its prompt now says what each world proves (staging: built and tested; Git: merged with green pipelines; release: deployed and healthy). The judging rule of the controller (ADR 0032) is unchanged otherwise.
