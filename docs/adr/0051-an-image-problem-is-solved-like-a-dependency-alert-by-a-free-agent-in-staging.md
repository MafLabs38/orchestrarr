# 0051 — An image that is behind, or run under a moving tag, is solved like a dependency alert: a free agent in staging, and the control plane audits the commit

- **Status**: proposed (accepted when the pull request that carries it is merged)
- **Date**: 2026-11-24
- **Spec**: D-105 ([SPEC.md](../SPEC.md)). Builds on [ADR 0048](0048-a-problem-with-no-pull-request-is-solved-by-a-free-agent-in-staging.md) (a problem with no pull request, the free agent that pushes its own branch) and [ADR 0028](0028-the-description-is-the-perimeter-and-the-version-lives-in-the-code.md) (the description is the perimeter of the code). Cases 04, 05 and 09 of [CASE-GRID.md](../CASE-GRID.md).

## Context

The table already shows the rows of the images: an image that has a newer version (`ImageUpdate`), an image run under a tag that moves (`FloatingImage`), and, from the base of a Dockerfile, an obsolete base image. They had **no action**: the problem has no pull request and the agent of ADR 0048 only worked on a dependency alert. The cases 04 (a newer image), 05 (a floating tag) and 09 (an obsolete base image) ask for the same thing as the case 03: a change that has to be **made**, by someone who can read the code, build and test it, then taken through the same worlds as any change.

## Decision

1. **An image row can be ticked on a host that can start a problem with no pull request** (`RelevancePolicy.ActOnProblems`, the development test bed), by the same gates as a dependency alert and by the same chain in `DecisionTable`: granted, described, a runner with a free agent that accepts the repository, a way back, a check after the deploy. Only the reasons that leave an agent something to do count: *no action defined* and *a major version to plan*. An image that the platform or an automation keeps, that is not written in the code that was read, or that differs from what runs, stays without an action, with its own reason.
2. **A major image is not a row that waits.** *Plan it* is what the lead agent does: the lead marks the problem complex (a major jump of a web server may break its configuration), and the controller judges the staging on what the objective asks.
3. **The lead is told where the image is written**: the file, the line and the reference as written, from what the control plane read of the code, as data with the problem. The free agent decides the rest, as for a dependency alert (ADR 0048). The commit is audited by the control plane on the forge (`ChangePolicy`) and the gateway opens the pull request from the branch.
4. **A Dockerfile is a place where an image is written.** A new kind of file of the description, `dockerfile`, reads the `FROM` lines: not a stage that an earlier line named, not `scratch`, not a variable. Without it a base image is *not in the scope* of the description, and its row has no action.
5. **How far the problem goes is the application's grant, not what the row shows.** On the test bed the grant is one world for every application (`Missions:SandboxMaxWorld`); an application can have a **lower** one (`Missions:SandboxWorldLimits`, by application id). A compose stack is not brought up by the release runner (ADR 0043), so the test bed grants it the Git world: its problems end at the merge.

## Consequences

- The case 05 (a floating tag is pinned) and the case 09 (a base image is rebuilt on a supported one, tests pass, redeployed) are played end to end by their scenarios. The case 04 is played up to the merge: **the deployment of a compose stack, and the check that follows, are not played** (the release runner does not deploy a compose file; a deployment through the stack's own delivery is not built on the test bed).
- The table says *what the row would do* (`goesUpTo`), but the world that a problem reaches is the intersection of the tick, the grant and the mandate. On the test bed the runner does not announce the merge to the table (the interim of ADR 0042), so most rows read *no runner* at the merge level while the chain still goes up to the release: the display and the power are two things there, to be reconciled when the typed tasks of ADR 0038 are gone. Not changed here.

## Not decided here, not built

- Where the facts about an image come from in a deployment (the registry, Portarr's reports): the test bed reads them from the table as it is built from Portarr's fake. A deployment reads them through the same source; nothing is added here.
- An image written in a Terraform file or a cluster manifest is read by the existing kinds; the cases are played on a compose file and a Dockerfile only.
- The deployment of a compose stack, a cluster image and their health check: not built.
