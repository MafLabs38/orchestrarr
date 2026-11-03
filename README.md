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

The GitHub repository, its branches, rulesets and labels are declared as code in `repos/orchestrarr.yaml` of `MafLabs38/github-template`: change a setting with a pull request there, never in the GitHub settings. `ci.yml`, `codeql.yml` and `mutation.yml` are the pipeline (see [docs/TESTING.md](docs/TESTING.md)). CodeQL runs but does not gate merges: the ruleset waits for CodeQL results, so a skipped CodeQL would block every merge.

Before a push, check that no real infrastructure value slipped in (`gitleaks dir .`).

## Run in the sandbox

Needs the .NET 10 SDK and Node 22.

```bash
# API, with the sample data of samples/ (http://127.0.0.1:7878)
dotnet run --project src/Orchestrarr.Host

# Interface with hot reload (http://127.0.0.1:5178), proxying /api to the API
cd frontend && yarn install && yarn dev

# Tests and lint (see docs/TESTING.md)
dotnet test --solution Orchestrarr.slnx && dotnet format Orchestrarr.slnx --verify-no-changes
cd frontend && yarn test && yarn lint && yarn stylelint && yarn format:check && yarn typecheck
```

To read a real Portarr instead of the sample, set `Portarr:BaseUrl` and give the key as `Portarr:ApiKey` through the environment (`Portarr__ApiKey`) or `dotnet user-secrets`: the key never goes in a file that is committed. Redirects are not followed, so the key only goes to that address; use https or a private network.

To read the dependency pull requests, give a GitHub token that can **only read** as `GitHub:Token` (`GitHub__Token`, or `dotnet user-secrets`): a fine-grained token with *Pull requests: read*, *Checks: read* and *Commit statuses: read* on the repositories (without the last one, the checks of a repository that reports commit statuses stay unknown). Without a token the screens say the pull requests were not read. The token only goes to the configured API address, which must be https (redirects are not followed). The pull requests of the bots of `GitHub:BotLogins` are read (default: Dependabot and Renovate); a list in the configuration replaces the default. For a demo without GitHub, `Forge:SamplePullRequestsFile` reads a file of the shape of `samples/github-pull-requests.sample.json`.

> **Security.** The API has **no authentication yet** (SPEC Q-12) and the image listens on all interfaces. It reads and shows the state of your fleet: put it behind an authenticating front (single sign-on, a private network) and do not publish its port.

## License

GPL-3.0, like the *arr apps (to be confirmed in the spec).
