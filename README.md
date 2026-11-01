# Orchestrarr

> Act. A self-hosted orchestrator agent that coordinates worker agents to fix, update and verify, in the spirit of the *arr suite.

**Status: inception.** No code yet; the need is being gathered. See [docs/VISION.md](docs/VISION.md).

Orchestrarr reads the findings of [Portarr](https://github.com/MafLabs38/portarr) (vulnerable dependencies, outdated images, broken applications), plans the work, delegates it to worker agents, verifies that nothing regressed, and proposes the result as a pull request for a human to approve. Safety and approval gates are core concerns: no direct push, sandboxed workers, least privilege, bounded budgets, full audit trail. It works with Mnemarr, the companion long-term memory.

## Documentation

| Document | Content |
|---|---|
| [docs/VISION.md](docs/VISION.md) | Why, what it must do, relation to Portarr, safety model, open questions |
| [CLAUDE.md](CLAUDE.md) | Rules for AI assistants working in this repository |

## Repository settings

The GitHub repository, its branches, rulesets and labels are declared as code in `repos/orchestrarr.yaml` of `MafLabs38/github-template`: change a setting with a pull request there, never in the GitHub settings. `ci.yml` is the CI of the first verticals; CodeQL is off in that file until its workflow is added (the ruleset waits for CodeQL results, so turning it on without the workflow blocks every merge).

Before a push, check that no real infrastructure value slipped in (`gitleaks dir .`).

## Run in the sandbox

Needs the .NET 10 SDK and Node 22.

```bash
# API, with the sample data of samples/ (http://127.0.0.1:7878)
dotnet run --project src/Orchestrarr.Host

# Interface with hot reload (http://127.0.0.1:5178), proxying /api to the API
cd frontend && yarn install && yarn dev

# Tests
dotnet test --solution Orchestrarr.slnx
cd frontend && yarn test
```

To read a real Portarr instead of the sample, set `Portarr:BaseUrl` and export a Portarr API key in `ORCHESTRARR_PORTARR_APIKEY` (the variable name is `Portarr:ApiKeyEnv`; the key itself never goes in a file).

## License

GPL-3.0, like the *arr apps (to be confirmed in the spec).
