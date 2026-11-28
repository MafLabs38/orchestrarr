# 0058 — A release that fails after its merge is fixed forward, and a failure its runner reports is never overruled

- **Status**: proposed (accepted when the pull request that carries it is merged)
- **Date**: 2026-11-28
- **Spec**: D-112 ([SPEC.md](../SPEC.md)), FR-VER-1 and FR-WLD-13 ([FUNCTIONAL.md](../FUNCTIONAL.md)), CASE-06 ([USE-CASES.md](../USE-CASES.md)). Carries out [ADR 0040](0040-the-way-back-is-a-change-the-loop-makes.md) (a release that goes wrong is a failed push that the loop takes up again) with the fix forward of [ADR 0055](0055-the-flow-is-a-fab-lots-routes-machines-and-rules-that-keep-it-moving.md). Answers Q-06.

## Context

Case 06 of the grid (a change green in its tests and broken at run time) was played for the first time on 2026-11-28, with a Flask 3 bump of a Python application whose tests run on Python 3.12 and whose image runs Python 3.7. It found two defects:

1. **A release that did not deploy was granted.** The image did not build on the deployment machine, so the version before it kept running. The runner reported the failure (`deploy:exit=1`), but the control plane's own check of the health route was answered by the version before, and the controller took that fact for a verified release.
2. **A failed release went back to a problem that no longer exists.** The loop started again in the first world with the problem as it was approved: a pull request that the merge had closed or replaced. The staging world had nothing to prove, and the controller sent it back until the loop gave up, or ended the problem.

The maintainer had answered Q-06 on 2026-11-25: the check after a deployment is the health check of the application, with no tester agent and no declared routes; an application that needs more runs its own end-to-end tests in its pipeline.

## Decision

1. **A release is granted only on its runner's word that the deployment and its verification passed** (`deploy:exit=0` and `verify:exit=0`, `MissionEvidence.ReleaseVerified`), besides the facts: what answers on the health route may be the version before. A grant without that word is overruled by the code: when the runner says the release failed (`MissionEvidence.ReleaseFailureOf`), it is routed as a release that did not verify; otherwise it goes to a person. **Only a grant is overruled**: an end or an escalation of the controller stands, since a claim of failure can only stop work; the controller is told that a release that did not deploy is a send-back, which takes the loop up again.
2. **A release that fails after its merge is fixed forward in staging**, as a merge that turns the integration branch red is (ADR 0055).
   - The change is in, so the problem goes back to staging as a problem with no pull request.
   - A free agent works on the integration branch as it is, told what failed and nothing of the output: the step its runner reports as failed (the deployment or the verification); a red pipeline on the released commit, fixed forward as a red pipeline (ADR 0055); otherwise that the controller judged it not to verify. It reproduces the build the way production does it.
   - A release approved by a standing rule that fails is still escalated (FR-WLD-13).
   - Same approval, raised priority, and the bound of the fixes forward (`Missions:MaxFixesForwardPerProblem`); past it, a person decides.
   - An application with no staging has no world to fix it forward in, and going back to the Git world would replay a pull request that the merge closed: a person decides.
3. **The image an application runs on is raised to the smallest newer version of the same image that the change needs**, never replaced by another image: a runtime replaced is the maintainer's decision (ADR 0055, the maintainer's rule of proportion). The check of the code reads, in a free agent's commit, every container file (`Dockerfile*`, `Containerfile*`, `*.dockerfile`) whole before and after the change, resolves the final `FROM` as Docker does (comment and blank lines left out, continued lines, the `ARG`s before the first `FROM`, several on a line, `${X:-default}`, the stages it is built from), and refuses a file whose final stage runs on another image (only a newer tag, digest or the default registry spelled out pass), whose image cannot be resolved in the file, that holds a heredoc, whose escape directive changed, or that could not be read whole (`ChangePolicy.SwappedBaseImage`). What a build runs on the way (a build stage, a frontend, a `--from`, a `RUN` line) is judged by the controller on the patch, like any line of the change: the isolation of the staging machine is what contains a build, not this check. The controller grants a newer version of the same image as maintenance when it is the smallest the change needs and it builds, starts and passes. Another runtime, a rewrite or features removed stay the maintainer's; a refusal is an open point the controller reads and escalates.
4. **The verification after a deployment is the health check** (the maintainer, 2026-11-25, Q-06): the application answers its health route within a minute. No tester agent and no declared routes.

## As built

- `MissionEvidence.ReleaseVerified` and `ReleaseFailureOf`; the override of a grant in `ProblemConductor.DecideAsync` for the release world.
- `BackToFirstWorldAsync`: a fix forward entry with the failed step (`FixForwardEntry(…, releaseFailure)`) when the application has a staging, an escalation when it has none.
- `ChangePolicy.SwappedBaseImage`; the controller prompt (the smallest newer version of the same image; a failed release is a send-back).
- `lab/scenarios/CASE-06.json`: the release fails, is fixed forward, and the new release is verified. The checks: the bump's pull request is closed, the fix forward is merged in the run, Flask is at least 3.0.3 and the base image at least Python 3.8.

## What this does not do

- The check is `GET /health` on the published port answering 200 within a minute. An application that has no such route fails its verification and is fixed forward as a release that did not verify. A health route that answers while the application is broken is not seen (Q-06).
- The control plane does not tell which version answers its health check; it relies on the runner's report of the deployment, which can only stop work.
- An image that has no newer tag of itself (an image line at its end, a deprecated image name replaced by its successor) cannot be fixed by the product: replacing a runtime is the maintainer's decision and the check refuses it on every free agent's branch, so the problem is escalated and the maintainer makes that change by hand. An approval that names the replacement image (part of the frame, read by the check) would be a later vertical, the maintainer's to decide.

## Consequences

- When the image does not build, the version that ran before keeps running while the release is fixed forward. When the new container does not start or does not answer its health route, the version before has already been replaced, and production stays down or broken until the fix forward is released (nothing is rolled back, ADR 0040). Nothing is promoted on the word of the previous version.
- A release that failed costs a fix forward in staging (about ten minutes on the test bed) instead of ending the problem.
