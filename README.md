# Orchestratarr

> Act. A self-hosted orchestrator agent that coordinates worker agents to fix, update and verify, in the spirit of the *arr suite.

**Status: inception.** No code yet; the need is being gathered. See [docs/VISION.md](docs/VISION.md).

Orchestratarr reads the findings of [Portarr](https://github.com/MafLabs38/portarr) (vulnerable dependencies, outdated images, broken applications), plans the work, delegates it to worker agents, verifies that nothing regressed, and proposes the result as a pull request for a human to approve. Safety and approval gates are core concerns: no direct push, sandboxed workers, least privilege, bounded budgets, full audit trail. It works with Mnemarr, the companion long-term memory.

## Documentation

| Document | Content |
|---|---|
| [docs/VISION.md](docs/VISION.md) | Why, what it must do, relation to Portarr, safety model, open questions |
| [CLAUDE.md](CLAUDE.md) | Rules for AI assistants working in this repository |

## Bootstrap

The GitHub repository is created by Terraform from `repos/orchestratarr.yaml` in `MafLabs38/github-template` (pull request `repo/add-mnemarr-orchestratarr`). Once that pull request is merged and the apply has run, publish this local repository through a scaffolding pull request:

```bash
cd path/to/orchestratarr
git remote add origin https://github.com/MafLabs38/orchestratarr.git
env -u GH_TOKEN git fetch -q origin develop
git checkout -q -B chore/initial-scaffolding
git reset -q origin/develop            # keeps the local files, rebases on the remote history
git add -A && git commit -m "chore: initial project scaffolding"
env -u GH_TOKEN git push -u origin chore/initial-scaffolding
env -u GH_TOKEN gh pr create -R MafLabs38/orchestratarr --base develop --title "chore: initial scaffolding" --body "Vision, project rules, common templates."
```

Before the first push, check that no real infrastructure value slipped in (`gitleaks dir .`). CI workflows (`ci.yml`, `codeql.yml`, `mutation.yml`) are added with the first vertical, when there is code for them to build; the options are declared in `repos/orchestratarr.yaml` (`codeql` and `mutation` on, `sonarqube` and `ai_review` off until their tokens exist).

## License

GPL-3.0, like the *arr apps (to be confirmed in the spec).
