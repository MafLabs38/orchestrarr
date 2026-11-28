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
   - A free agent works on the integration branch as it is, told only the step that failed (the deployment or the verification), never the output of the build or of the application, which is the repository's text. It reproduces the build the way production does it.
   - Same approval, raised priority, and the bound of the fixes forward (`Missions:MaxFixesForwardPerProblem`); past it, a person decides.
   - An application with no staging has no world to fix it forward in, and going back to the Git world would replay a pull request that the merge closed: a person decides.
3. **A base image is raised to the smallest newer version of the same image that the change needs**, never replaced by another image. The check of the code refuses a Dockerfile change that takes an image out of its `FROM` lines and brings another in (`ChangePolicy.SwappedBaseImage`), and the controller grants a newer version of the same image as maintenance when it is the smallest the change needs and it builds, starts and passes. Another runtime, a rewrite or features removed stay the maintainer's.
4. **The verification after a deployment is the health check** (the maintainer, 2026-11-25, Q-06): the application answers its health route within a minute. No tester agent and no declared routes.

## As built

- `MissionEvidence.ReleaseVerified` and `ReleaseFailureOf`; the override of a grant in `ProblemConductor.DecideAsync` for the release world.
- `BackToFirstWorldAsync`: a fix forward entry with the failed step (`FixForwardEntry(…, releaseFailure)`) when the application has a staging, an escalation when it has none.
- `ChangePolicy.SwappedBaseImage`; the controller prompt (the smallest newer version of the same image; a failed release is a send-back).
- `lab/scenarios/CASE-06.json`: the release fails, is fixed forward, and the new release is verified. The checks: the bump's pull request is closed, the fix forward is merged in the run, Flask is at least 3.0.3 and the base image at least Python 3.8.

## What this does not do

- A health route that the application does not have, or that answers while the application is broken, is not seen: the check is what the application offers (Q-06).
- The control plane does not tell which version answers its health check; it relies on the runner's report of the deployment, which can only stop work.

## Consequences

- The production that ran before keeps running while a release that cannot deploy is fixed forward. Nothing is rolled back by hand, and nothing is promoted on the word of the previous version.
- A release that failed costs a fix forward in staging (about ten minutes on the test bed) instead of ending the problem.
