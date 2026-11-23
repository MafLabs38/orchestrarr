# 0048 — A problem with no pull request is solved by a free agent that pushes its own branch, and the control plane audits that commit

- **Status**: proposed (accepted when the pull request that carries it is merged)
- **Date**: 2026-11-22, reshaped 2026-11-23 on the maintainer's design
- **Spec**: D-102 ([SPEC.md](../SPEC.md)). Builds on [ADR 0044](0044-a-worker-agent-repairs-the-source-in-staging-and-the-gateway-carries-it.md) (the repair agent and the gateway), [ADR 0046](0046-a-lead-agent-thinks-and-workers-act-on-one-world-with-one-instruction.md) (the lead, workers with one instruction) and [ADR 0047](0047-a-machine-has-a-state-and-leaves-a-reset-isolated-and-qualified.md) (a machine that is isolated by design). Cases 03 and 16 of [CASE-GRID.md](../CASE-GRID.md).

## Context

Until now every row that could be started was a pull request: something to prove, and, when it did not pass, to repair on paper. The other rows (an alert on a dependency, an abandoned library) were shown and could not be ticked. The cases of the dependencies are about a problem that **has no change yet**: a vulnerable version, with or without a fixed one; a library that nobody maintains. The change has to be **made**, by someone who can read the code, install, build and test. The repair agent of ADR 0044 cannot: it edits a few source files in a container with no network and is forbidden to touch the dependencies.

The maintainer decided on 2026-11-22 that the worker of the staging world is **free inside its machine** (ADR 0047: the machine is the boundary), and that the control plane holds the whole problem and gives the worker only its instruction (ADR 0046).

A first version of this decision carried the agent's files out of the machine through the runner, which read them back and wrote a diff. **That was a design defect, and the maintainer named it** (2026-11-23): a developer who has cloned a repository and changed it does not hand files over; he commits and pushes, and it is that commit, on the forge, that is reviewed. Judging what a machine says of its own work lets the machine shape the judgement; judging the commit on the forge does not.

## How it works (the maintainer's model)

1. **The agent works in its world, in the staging machine**, as a developer would: it clones, changes, installs, builds, tests, breaks things. It has no rule about how.
2. **It commits and pushes its own branch to the forge, as often as it wants.** Pushing saves work; **pushing validates nothing**. The agent's identity can write branches and do nothing else: no pull request, no merge, no workflow, no administration, and the default branch refuses direct pushes by the rules of the repository (no actor may bypass them).
3. **It ends by saying it has finished**: "I am done, read what I pushed". That is the handover of the mission, and it carries the **name of the branch and the commit it finished at**. What it pushes after that is not what is audited.
4. **The control plane reads that commit on the forge**, never what the machine says of it: where the branch is (it must be at that commit), what it changes against the default branch (the files, the lines, whether the default branch moved since), and it applies the policy of what may be integrated. The controller judges that reading, which the control plane gives it itself.
5. **If it accepts, it integrates**: the act gateway opens the pull request **from the agent's branch at the audited commit** (nothing is opened if the branch has moved), and the Git and release worlds merge and deploy it as any other pull request, on the pipeline of the pull request. If it does not accept, nothing is opened: the branch stays a branch, and the work goes back to the agent or to the maintainer.
6. **The agent never holds the right to validate.** The pull request is opened and merged with the control plane's own credential, which no runner holds. Whether the runner or the control plane opens the pull request would be as safe either way, since a pull request that the control plane does not accept is not merged; the gateway opens it, so that there is one writer for the product, under a mandate and in the audit trail.

The danger is **integration, not pushing**. A branch that the control plane does not integrate costs a little CI; the policy and the controller stand between a branch and a merge.

## Decision

1. **A row that has no pull request can be ticked** on a host that can start such a problem (the development test bed: `RelevancePolicy.ActOnProblems`, set by `Missions:Sandbox`; elsewhere the row is shown with no action), when the application is described and a runner that says in its hello that it has a free agent takes the work (the same gates as a pull request row, one chain in `DecisionTable`; the change is taken for as large as a major one: it goes to production by itself only for an application that promotes even a major change by itself). The tick binds to the row as it is: there is no commit to bind to.
2. **The lead plans from the findings.** The findings of the source (an advisory with the installed and the fixed version or none, an abandoned library with a suggested replacement) are given to the lead as untrusted data with the description; it writes the instruction of the staging worker (the facts the worker needs, never the commands), names only what the data names, and marks the problem complex when a library is replaced.
3. **The staging worker is a free agent** (Claude Code, root in a container, the docker socket of the machine, the network, on a clone of the default branch), inside the machine of ADR 0047. No short limit on its time (generous settings: `FreeMaxTurns`, `FreeTimeoutMinutes`). Its two tokens (the subscription's and the one that pushes branches) reach it by the standard input of the connection, and git gets the second through a helper that reads the environment: in no file and no command line.
4. **The runner is the carrier, not the judge.** It starts the agent, then asks the forge where the branch is, runs the steps of the repository **on that commit, in a clean tree**, as evidence for the controller, and hands over the branch and the commit. It reads no file and writes no diff.
5. **The control plane audits the commit** (`ChangePolicy`, on the forge's own reading): never the pipelines, the files that carry secrets, the files that say where dependencies come from (`.npmrc` and the like), the files that instruct an agent, no deletion or rename, no binary file, bounded count and size, the paths that the frame of the mission forbids, and the default branch must not have moved since the clone. A change that fails is refused at the handover and no pull request is opened.
6. **The controller judges the staging world on what the objective asks, and on the control plane's own reading** (the size of every file and the lines of those that a tool did not write, cut and said so when long): the named findings fixed (a version at or above the fixed one), the steps passing, and, for a finding with **no fixed version**, the exposure removed another way, or an escalation. A count of alerts larger than the findings is what the source reports, not necessarily all that the control plane knows. The later worlds judge only their own objective.
7. **The source of findings is a seam.** `IDependencyFindings`: on the test bed a JSON file that the scenarios of the lab write (`Missions:SandboxFindingsFile`); a deployment reads Portarr and the scanners through a provider. It says nothing when it cannot answer, never a guess.

## Consequences

- The cases 03 (with and without a fixed version) and 16 are played end to end by their scenarios; the grid is ticked from the replays.
- The root agent is the largest power the product gives an agent, and it is acceptable **only because** of ADR 0047 (isolation by design, qualification before every use, reset after it), because nothing it produces is believed (the control plane reads the commit on the forge and applies the policy itself), and because it cannot integrate anything.
- A new power is given to the agent: **a token that writes branches**. The permission to write contents is all it has. Pushing is not the risk the maintainer sees; the rules of the repository (no bypass, a pull request and the required check) and the control plane's audit are what stand between a branch and the default branch.
- The image of the free agent (`docker/agent-free`) is built on the staging machine when it is missing (a reset removes it), tagged with the hash of its Dockerfile, pinned by digest and by checksum, and bumped by Dependabot.
- The staging run of the steps is **evidence for the controller, not the last guard**: the pipeline of the pull request on the forge, on the exact commit, is what the merge waits for.
- The old path of ADR 0044 (the repair of a pull request that does not pass, which carries files) is left as it is; aligning it with this model is a later change.

## Residual risks, accepted for the test bed

- **The CI of a pushed branch runs the agent's code**, with whatever the repository exposes to its branches. On a real project the secrets of the repository belong to the protected branch or to an environment, as for any developer; the sandbox repositories hold none. The token cannot change a workflow file.
- **The subscription token and the push token are in the agent's container**, on a machine with the Internet: a prompt injection in a package could send them out. The push token can only write branches of the sandbox; a short-lived token per job is the way to close it.
- **A subtle line in a permitted file** (a script of `package.json`, a test weakened) passes the policy and depends on the controller reading the lines. The control plane gives it the full reading, with a cut said.
- **The facts of a change with no pull request** (the way back, tags, write credentials) are declared, as for the repair of ADR 0044; the diff and the paths are read on the forge. CI is declared green only at the crossing into the Git world, where no pipeline has run on the change yet, and unknown at an act.

## Not decided here, not built

- Where the findings come from in a deployment (Portarr's reports, a scanner): **no such provider is built**, a deployment has none of this path. And how a finding is tied to a row when a repository has several rows with no pull request: the test bed has one row per repository, and the findings are read for a dependency alert only.
- The mail that tells the maintainer at once (the case 03 frame) and the complex flag beyond the lead's mark: the report of the cases 14 and 03 is its own lot.
- A branch that the control plane refuses is not deleted yet.
