# The lab

Tools to play the cases of [docs/CASE-GRID.md](../docs/CASE-GRID.md) again and again on the test-bed repositories (a public GitHub organisation of its own, never a real project).

```bash
export LAB_ORG=<test-bed organisation> GH_TOKEN=<a token that may write there>
lab/sandbox.sh status                 # each repository against its seed, the open pull requests
lab/sandbox.sh mark-seed <repo> <ref> # the commit that is the seed of a repository (tag lab-seed)
lab/sandbox.sh reset [repo...]        # back to the seed
```

- The **seed** of a repository is the commit tagged `lab-seed`: the deliberately broken project as it was first published, with the maintenance that its pipeline needs to run at all.
- A **reset** closes the open pull requests of the lab and of the agents (a bot's pull request is left alone: a closed one is never opened again by the bot), deletes the work branches, and restores the seed tree **through a pull request** that passes the repository's checks and is squash-merged. The repository rules (pull request required, no force push) are the ones of a real project and are never changed.
- The staging virtual machines are reset by their own tool (`lab/lab.sh` of the staging lab).
- Nothing here is a secret; the organisation and the token come from the environment.

## Playing a scenario

A scenario (`lab/scenarios/<case>.json`) names the faults to plant, the rows to tick and what must be true at the end. The runner does the whole case, the way the maintainer does it, and exits 0 only when every expectation holds:

```bash
export LAB_URL=<control plane> LAB_USER=<account> LAB_PASSWORD_FILE=<file> LAB_ORG=<test-bed organisation> GH_TOKEN=<token>
export NODE_PATH=<a folder whose node_modules has playwright>
node lab/e2e/run-scenario.cjs lab/scenarios/CASE-01.json
```

1. reset the repositories of the scenario to their seed; 2. plant the faults; 3. sign in, refresh the table until it shows exactly the planted pull requests, tick, validate; 4. follow the missions of this validation, and read the final state on the forge (not in the interface); 5. reset again.
`LAB_KEEP=1` leaves what the run made, `LAB_SKIP_SETUP=1` plays on what is there, `LAB_MAX_SECONDS` bounds the wait.
