# 0049 — Problems on one repository are done one at a time, and a conflict is not mended by an agent that stands in the way of the others

- **Status**: proposed (accepted when the pull request that carries it is merged)
- **Date**: 2026-11-24
- **Spec**: D-103 ([SPEC.md](../SPEC.md)). Builds on FR-MIS-6 ([FUNCTIONAL.md](../FUNCTIONAL.md): one problem at a time on a repository), [ADR 0042](0042-a-runner-serves-one-world-and-the-test-bed-validates-through-the-plan.md) (the plan that starts a problem) and [ADR 0048](0048-a-problem-with-no-pull-request-is-solved-by-a-free-agent-in-staging.md). Case 07 of [CASE-GRID.md](../CASE-GRID.md).

## Context

Case 07 asks what happens when many pull requests of one repository are ticked together: an order, a grouping, the conflicts between them, and no ten deployments if one will do. A first reading was a batch: order the pull requests by risk, test their combined result once, merge them in turn and deploy once.

The maintainer set it aside in the chat of 2026-11-24: this is a **Git flow** problem, not a grouping problem. Treating the pull requests one after the other is not a defect; treating them one after the other *carelessly* (without merging one before starting the next) is. So the rule is plain: **no work site starts on a repository while another is going on there.**

## Decision

1. **One work site at a time on a repository.** Problems that are validated together on one repository are done in the order they were validated, each one through its worlds (staging, merge, release) before the next one starts. A problem that has to wait is **planned but not started**: no mandate is issued, no approval is used, so what waits does not expire (the lifetime of a mandate is an hour, and ten problems do not fit in it). It starts when no other problem is in progress on its repository (`ProblemQueue`, `ProblemQueueDriver`; the response of the validation says which rows wait). The line is in memory like the other stores of the test bed.
2. **A problem that does not succeed does not hold the others.** It ends (done, failed, escalated, stopped) and the next one starts. A change that is not sure is never merged: nothing is opened or merged on a failure, and the queue goes on.
3. **A refusal that no agent mends ends the problem at once.** When the runner stops before building because the pull request **conflicts with its base** (the base moved: an earlier problem of the repository was merged) or **moved since the maintainer approved it**, it says so with a marker in the dossier (`refused:conflict`, `refused:moved`). The control plane, by its code and without asking the controller, ends the problem: the controller would send it back, the same worker could not mend it, and the loop would use its three send-backs for nothing. The marker is a claim of the runner and can only stop work, never widen a power.
4. **What a conflict then needs is a new pull request**: a dependency bot makes its own again on the new base (the lab plays that bot: `lab/sandbox.sh rebase`), and the maintainer ticks it again, on the commit it is now at (the approval is of a commit, so a head that moves is validated again).

## Not decided here, not built

- **An agent of the Git world that resolves a conflict** is the maintainer's intended fallback, and it is not built: the rules above are meant to keep the case from arising (it arises only when something moves the base outside this product, or by timing). It is its own change.
- **Grouping, ordering by risk and one deployment for many pull requests** are not built: with one problem at a time each merged pull request is deployed by its own problem. A shared deployment is a possible later optimisation, never a condition of correctness.
- The line is not kept across a restart of the control plane, and it is shown only by the answer to the validation and the trail (`problem.queued`, `problem.queue-refused`).
