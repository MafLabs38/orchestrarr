# Orchestrarr — Testing and quality

> Same strategy as Portarr's ([TESTING.md](https://github.com/MafLabs38/portarr/blob/develop/docs/TESTING.md)), scaled to what exists. The three products share conventions.

## 1. What is tested, and where

| Level | Tool | What it proves |
|---|---|---|
| Backend unit | xUnit v3, AwesomeAssertions, NSubstitute, `FakeTimeProvider` | The domain (work items, plans, the cached catalog) and the providers (mapping a source's report to the fleet model) |
| API | `WebApplicationFactory` | The REST contract: routes, JSON shape, 404, that nothing can act yet |
| Frontend | Vitest, Testing Library (`happy-dom`) | The screens and components: what is shown, what is ticked, what is disabled and why |
| End to end | Playwright | Not yet. It arrives with the first vertical that has a write path |
| Acceptance | The rotten test repository (`SCENARIOS.md` of `orchestrarr-testbed`) | Find, fix, merge, deploy and roll back on a real application |

Rules: a test protects a behavior someone relies on, runs in milliseconds, and fails for one reason. Time is a `FakeTimeProvider`, never a sleep. **Safety behaviours are tested like features**: a read-only vertical proves it has no route that acts; a step that cannot be done is unavailable and unticked; a power that is off is not offered.

## 2. Other automated checks

| Check | Tool | When |
|---|---|---|
| Backend lint | .NET analyzers (warnings are errors), `dotnet format --verify-no-changes` | PR |
| Frontend lint | ESLint (strict, SonarJS, a11y, import order), Stylelint, Prettier, `tsc --noEmit` | PR |
| Quality and coverage | SonarQube Quality Gate | full lane |
| Static security analysis | CodeQL (C#, TypeScript, workflows), `security-and-quality` queries | PR (skippable) and weekly |
| Secrets | gitleaks | PR |
| Vulnerabilities | Trivy (dependencies and configuration), Dependabot | PR and weekly |
| Mutation testing | Stryker.NET (Core, providers), StrykerJS (frontend): do the tests catch injected bugs? | weekly, never blocking |
| AI review | The four agents of `.claude/agents/` (tests, quality, security, architecture) | before a vertical PR, locally with the `review-vertical` skill |

## 3. The two lanes, and skipping

- **Light lane** (a pull request into `develop`): lint, backend and frontend tests with coverage, security scans.
- **Full lane** adds SonarQube. It runs on every push to `develop` and `main`, on every pull request into `main`, on a pull request labelled `full-ci` (add the label before pushing) and on demand.
- **Skipping.** A **draft** pull request, or one labelled **`skip-quality`**, stays on the light lane and CodeQL does not run on it: the long checks run when it is marked ready, or never when skipped on purpose. Build and tests always run.
- Only `develop` and `main` publish an image; a pull request never does.

## 4. Thresholds

| Rule | Threshold |
|---|---|
| Frontend line and statement coverage | ≥ 70 % (enforced by `yarn test:coverage`) |
| Backend coverage | Reported to SonarQube; the Quality Gate decides (new code) |
| Unit tests | 100 % green, whole suite under 3 minutes |
| Lint | Zero warning, zero error |
| CodeQL | No high or critical alert on new code, once the gate is on |
| Mutation score | Reported, not enforced: a survivor in code a vertical relies on is a missing test, in glue code it is not |

## 5. Commands

```bash
dotnet test --solution Orchestrarr.slnx                       # backend
dotnet format Orchestrarr.slnx --verify-no-changes            # backend format
cd frontend
yarn test                                                     # frontend tests
yarn test:coverage                                            # with the coverage threshold
yarn lint && yarn stylelint && yarn format:check && yarn typecheck
yarn lint:fix && yarn format                                  # fix what can be fixed
```

On a machine with limited CPU, run heavy builds through the resource-limited slice (`systemd-run --user --slice=build-limit.slice --scope nice -n 15 …`) when it exists.

## 6. Before a vertical's pull request

Run the **`review-vertical`** skill: the four review agents run in parallel on the branch, and their findings are triaged (fixed, or declined with the reason) in the PR's *Review* section. An architecture verdict of "no" blocks the PR.

## 7. Seeing the code smells in the editor

The SonarQube rules can run in the editor (SonarLint) and be read by assistants, bound to the project of the SonarQube server.

- The binding is **local and never committed** (it holds the address of the server): `.sonarlint/connectedMode.json`, git-ignored.

  ```json
  { "sonarQubeUri": "https://sonarqube.homelab.example", "projectKey": "<owner>_<repository>" }
  ```

- Bind by the **address**, not by a connection id in `.vscode/settings.json`: an id that is not defined as a connection of the machine gives "The specified connection id doesn't exist", and the binding is invalid.
- The project is created on the server through its API (name, key `<owner>_<repository>`, main branch `develop`, the default Quality Gate), not by hand.
- An assistant's SonarQube server that runs in a container reaches the editor's SonarLint on `localhost` only with the host network (`--network=host`); without it the editor is never found and the server falls back on a weaker analysis. One editor window per port: the port tells which window is analysed.
