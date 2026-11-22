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
