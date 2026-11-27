# The lab

Tools to play the cases of [docs/CASE-GRID.md](../docs/CASE-GRID.md) again and again on the test-bed repositories (a public GitHub organisation of its own, never a real project).

This is a **development tool, not part of the product**. It writes to the organisation named by `LAB_ORG` (the test bed: the operator checks that it is), with the token it is given, and only to repositories whose seed is pinned in `lab/seeds.json` (a moved tag is refused): it closes pull requests, deletes branches, moves the `lab-seed` tag, pushes branches and squash-merges pull requests. It needs `git`, `gh`, `jq` and `npm`; the runner needs `node` and Playwright. Runs are serial: two at once close each other's pull requests.

```bash
export LAB_ORG=<test-bed organisation> GH_TOKEN=<a token that may write there>
lab/sandbox.sh status                 # each repository against its seed, the open pull requests
lab/sandbox.sh mark-seed <repo> <ref> # the commit that is the seed of a repository (tag lab-seed, pinned in lab/seeds.json)
lab/sandbox.sh reset <repo...>|--all  # back to the seed
lab/sandbox.sh plant <scenario.json>  # plant the faults of a scenario: a branch and a pull request for each
lab/sandbox.sh rebase <scenario.json> # what a dependency bot does when the base moved: the open pull requests of a scenario are made again on the base as it is now
```

Optional: `LAB_BASE_BRANCH` (`develop`), `LAB_WORKDIR` (the clones, `~/.cache/orchestrarr-lab`), `LAB_CHECK_TIMEOUT` (600 s), `LAB_REQUIRED_CHECK` (`build`: the check that must have reported and passed before a restore is merged), `LAB_BOT_AUTHORS` and `LAB_BOT_BRANCHES` (the authors and the branches that a reset leaves alone), `LAB_ACCOUNT` and `LAB_EMAIL` (the token's account and its no-reply address, read with `gh api user` when unset: the lab's commits must be linked to the account that opens the pull request, or the control plane does not follow a new head). The token is given to git through the environment (`lab/git-askpass.sh`), never written to a file or to a command line.

- The **seed** of a repository is the commit pinned in `lab/seeds.json` and tagged `lab-seed` on the forge (the tag is only where the commit is fetched from): the deliberately broken project as it was first published, with the maintenance that its pipeline needs to run at all. A seed marked `"red": true` has red pipelines as its fault (`pipeline-scenarios`): its restore does not wait for its checks, and its pipelines are then run on the base branch, so that the forge shows the default branch as red as the seed is, which is what the control plane reads to name the pipelines to repair.
- A **reset** closes the open pull requests of the lab and of the agents (a bot's pull request is left alone: a closed one is never opened again by the bot), deletes the work branches, and restores the seed tree **through a pull request** that passes the repository's checks and is squash-merged. The repository rules (pull request required, no force push) are the ones of a real project and are never changed.
- The staging virtual machines are reset by their own tool, outside this repository (the staging lab), which also puts their hypervisor firewall back **before** a machine starts, because a restore brings back the network settings of the snapshot (ADR 0047); their state and their isolation are checked by the runner at each qualification and shown on the Missions screen.
- Nothing here is a secret; the organisation and the token come from the environment.

## A demo

`lab/demo.sh` plants the faults of several cases together (`lab/scenarios/DEMO.json`: an express 5 pull request that needs a repair, **a grain of sand** (a critical advisory in the Node.js runtime itself, written in the findings of `pipeline-scenarios` with no fixed version, and **embargoed** (the security team's own, in no public database yet), so that an agent that searches the public databases cannot refute it: no change can remove it, a project in Node cannot do without Node, and the flow must put it aside while it finishes the other operations of that repository and of the others), the advisories of `shop-api`, the dependency bumps of `ledger-worker`, and the images of the seeds) and writes the findings of the fake source (`LAB_FINDINGS_FILE`). A repository that the release runner does not deploy (the compose stack of `edge-stack`, the pipelines alone of `pipeline-scenarios`) is given the Git world by the control plane (`Missions:SandboxWorldLimits`): its operations end at the merge. It ticks and validates nothing: a person does that on the screens, and follows the problems on the Missions screen. The problems of one repository go one at a time (ADR 0049), those of different repositories go together, through the **lanes** of the runner (ADR 0053: `MissionRunner:GitLanes` slots of the Git world, one lane per staging machine and one for the deployment machine): with one staging machine the staging world is the narrow place of a busy day, and a second machine is a second lane (a VM, a line in `Missions:Machines`, a line in `MissionRunner:StagingMachines`).

`lab/demo-bot.sh <scenario> [seconds]` is the dependency bot of the demo (ADR 0055): every half minute it makes again, on the base as it is now, the open pull requests of the scenario that are behind it, as Dependabot does after a merge; and, as Dependabot does, it closes a pull request whose version the base already holds; the control plane then closes that operation as done by another one. The control plane asks a bot to rebase (a comment) and waits; for the pull requests that the lab planted, this is what answers (the account of the lab is named in `Missions:BotAuthors` **and** in `Missions:BotRebaseCommands`, with any one-line command, such as `Missions__BotRebaseCommands__<account>=@dependabot rebase`: without one its pull requests are put aside at once). Start it before the demo and stop it after.

```bash
export LAB_ORG=<test-bed organisation> GH_TOKEN=<token> LAB_FINDINGS_FILE=<the Missions:SandboxFindingsFile of the control plane>
lab/demo.sh
```

## Playing a scenario

A scenario (`lab/scenarios/<case>.json`) names the faults to plant, the rows to tick and what must be true at the end. The runner does the whole case, the way the maintainer does it, and exits 0 only when every expectation holds:

```bash
export LAB_URL=<control plane> LAB_USER=<account> LAB_PASSWORD_FILE=<file> LAB_ORG=<test-bed organisation> GH_TOKEN=<token> LAB_FINDINGS_FILE=<the Missions:SandboxFindingsFile of the control plane, only for a scenario with findings>   # LAB_PASSWORD, if set, wins over the file
export NODE_PATH=<a folder whose node_modules has playwright>
node lab/e2e/run-scenario.cjs lab/scenarios/CASE-01.json
```

1. reset the repositories of the scenario to their seed; 2. plant the faults; 3. sign in, refresh the table until it shows the planted pull requests, tick, validate; 4. follow the missions of this validation, and read the final state on the forge (not in the interface); 5. reset again, whatever happened.
A scenario may also carry: `repos` (repositories to reset that it does not plant in), `findings` (what the fake source of advisories says of the dependencies of a repository, written to the file `LAB_FINDINGS_FILE`, which is the `Missions:SandboxFindingsFile` of the control plane, and emptied at the end), and in `expect`: `files` (a dependency of a `package.json` on the default branch at least at a version, or gone), `progress` (a sentence that the Missions screen showed, such as the lead's complexity mark), and `thisRun` on a pull request (only one opened since the run began counts: a fix has the same title at each run). **Two rounds** (`CASE-07`): `endsWithoutDone` is how many missions may end without being done in the first round (a pull request that conflicts with its base is ended at once, ADR 0049); `then` is a second round, played after a dependency bot has made the open pull requests of the scenario again on the new base (`lab/sandbox.sh rebase <scenario>`) and the table shows their new commits: its own `tick` and its own `expect` (the final state), while the first `expect` is checked after the first round.
`LAB_KEEP=1` leaves what the run made, `LAB_SKIP_SETUP=1` plays on what is there, `LAB_MAX_SECONDS` bounds the wait.

After the control plane of the test bed is restarted, check that its mission runner still claims (the table says `noRunner` when it does not) and restart it if it does not.
