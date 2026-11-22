# The lab

Tools to play the cases of [docs/CASE-GRID.md](../docs/CASE-GRID.md) again and again on the test-bed repositories (a public GitHub organisation of its own, never a real project).

This is a **development tool, not part of the product**. It writes to the organisation named by `LAB_ORG` (the test bed: the operator checks that it is), with the token it is given, and only to repositories that have a seed: it closes pull requests, deletes branches, moves the `lab-seed` tag, pushes branches and squash-merges pull requests. It needs `git`, `gh`, `jq` and `npm`; the runner needs `node` and Playwright. Runs are serial: two at once close each other's pull requests.

```bash
export LAB_ORG=<test-bed organisation> GH_TOKEN=<a token that may write there>
lab/sandbox.sh status                 # each repository against its seed, the open pull requests
lab/sandbox.sh mark-seed <repo> <ref> # the commit that is the seed of a repository (tag lab-seed)
lab/sandbox.sh reset <repo...>|--all  # back to the seed
lab/sandbox.sh plant <scenario.json>  # plant the faults of a scenario: a branch and a pull request for each
```

Optional: `LAB_BASE_BRANCH` (`develop`), `LAB_WORKDIR` (the clones, `~/.cache/orchestrarr-lab`), `LAB_CHECK_TIMEOUT` (600 s), `LAB_REQUIRED_CHECK` (`build`: the check that must have reported and passed before a restore is merged), `LAB_BOT_AUTHORS` (the authors whose pull requests a reset leaves alone). The token is given to git through the environment (`lab/git-askpass.sh`), never written to a file or to a command line.

- The **seed** of a repository is the commit tagged `lab-seed`: the deliberately broken project as it was first published, with the maintenance that its pipeline needs to run at all.
- A **reset** closes the open pull requests of the lab and of the agents (a bot's pull request is left alone: a closed one is never opened again by the bot), deletes the work branches, and restores the seed tree **through a pull request** that passes the repository's checks and is squash-merged. The repository rules (pull request required, no force push) are the ones of a real project and are never changed.
- The staging virtual machines are reset by their own tool, outside this repository (the staging lab).
- Nothing here is a secret; the organisation and the token come from the environment.

## Playing a scenario

A scenario (`lab/scenarios/<case>.json`) names the faults to plant, the rows to tick and what must be true at the end. The runner does the whole case, the way the maintainer does it, and exits 0 only when every expectation holds:

```bash
export LAB_URL=<control plane> LAB_USER=<account> LAB_PASSWORD_FILE=<file>   # or LAB_PASSWORD, which wins LAB_ORG=<test-bed organisation> GH_TOKEN=<token>
export NODE_PATH=<a folder whose node_modules has playwright>
node lab/e2e/run-scenario.cjs lab/scenarios/CASE-01.json
```

1. reset the repositories of the scenario to their seed; 2. plant the faults; 3. sign in, refresh the table until it shows the planted pull requests, tick, validate; 4. follow the missions of this validation, and read the final state on the forge (not in the interface); 5. reset again, whatever happened.
`LAB_KEEP=1` leaves what the run made, `LAB_SKIP_SETUP=1` plays on what is there, `LAB_MAX_SECONDS` bounds the wait.
