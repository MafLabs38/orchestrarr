# Orchestrarr — Testing and quality

> Same strategy as Portarr's ([TESTING.md](https://github.com/MafLabs38/portarr/blob/develop/docs/TESTING.md)), scaled to what exists. The three products share conventions.

## 1. What is tested, and where

| Level | Tool | What it proves |
|---|---|---|
| Backend unit | xUnit v3, AwesomeAssertions, NSubstitute, `FakeTimeProvider` | The domain (work items, plans, the cached catalog) and the providers (mapping a source's report to the fleet model) |
| API | `WebApplicationFactory` | The REST contract: routes, JSON shape, 404, that no route runs anything and the ones that record something answer only to a person with the right role; the probe `GET /health` and the security headers are checked. Most API tests are signed in by a test-only filter (an `Operator` by default, a role or nobody by header); the **real sign-in** (cookie, server-side session, lockout, CSRF header, the role × route matrix, the accounts) has its own tests through the real routes |
| Executor and second factor | xUnit v3; the pipeline with a fake control plane and a `FakeTimeProvider`, a real SQLite file for the executor's store; end-to-end tests that run the real executor against the real host (`ExecutorApiTests`) | That every check of [ADR 0017](adr/0017-orders-are-typed-tasks-approved-with-a-second-factor-that-the-executor-checks.md) refuses by itself (rights list, code, replay, journal, expiry, stop) and that the dry run starts nothing. A changed expected outcome is a finding, never a test to adjust (CLAUDE.md rule 14) |
| Storage | A real SQLite file in a temporary folder | What must survive a restart: persistence after reopening, the unique key of a decision, all-or-nothing batches, the append-only trail, the capacities, and the global stop (a new database is released, an unreadable one is engaged, a release that cannot be saved is refused) |
| Frontend | Vitest, Testing Library (`happy-dom`) | The screens and components: what is shown, what is ticked, what is disabled and why |
| End to end | Playwright, by hand against the sandbox | Looking at the real screens in a real browser, as the maintainer does (§6). No suite is committed yet: the scripts hold the address and the accounts of a sandbox, which never go in the repository |
| Acceptance | The rotten test repository (`SCENARIOS.md` of `orchestrarr-testbed`) | Find, fix, merge, deploy and roll back on a real application |

Rules: a test protects a behavior someone relies on, runs in milliseconds, and fails for one reason. Time is a `FakeTimeProvider`, never a sleep. **Safety behaviours are tested like features**: the tests prove that no route runs anything, that the runners that start a container change nothing outside it (git on the host never reads a folder a container can write, the agent has no shell and its token never reaches the trace), and that the ones that record something answer only to an operator or an administrator who signed in, with the header that only the interface sends; a step that cannot be done is unavailable and unticked; a power that is off is not offered.


## 2. Other automated checks

| Check | Tool | When |
|---|---|---|
| Backend lint | .NET analyzers and the Sonar C# analyzer (the SonarLint and SonarQube rules; warnings are errors), `dotnet format --verify-no-changes` | PR |
| Frontend lint | ESLint (strict, SonarJS, a11y, import order), Stylelint, Prettier, `tsc --noEmit` | PR |
| Quality and coverage | SonarQube Quality Gate | full lane |
| Static security analysis | CodeQL (C#, TypeScript, workflows), `security-and-quality` queries | PR (skippable) and weekly |
| Secrets | gitleaks | PR |
| Review decisions | `.github/workflows/review-decisions.yml`: a serious finding that is not fixed must cite the maintainer's "OK to decline R-<pr>-<n>" comment on the pull request (ADR 0022); the checker's own tests run in the `lint` job (`python3 -I -m unittest` in `tools/review-decisions`) | PR |
| Vulnerabilities | Trivy (dependencies and configuration), Dependabot | PR and weekly |
| Mutation testing | Stryker.NET (Core, providers; the storage is not mutated yet, its tests use a real file), StrykerJS (frontend): do the tests catch injected bugs? | weekly, never blocking |
| AI review (advisory) | The five agents of `.claude/agents/` (tests, quality and spec on Sonnet, security and architecture on Opus), one comment each on the pull request, on the maintainer's subscription token | when a PR is opened or marked ready, and again with the `ai-review` label; not with `skip-quality`; locally before a vertical PR with the `review-vertical` skill |

## 3. The two lanes, and skipping

- **Light lane** (a pull request into `develop`): lint, backend and frontend tests with coverage, security scans.
- **Full lane** adds SonarQube. It runs on every push to `develop` and `main`, on every pull request into `main`, on a pull request labelled `full-ci` (add the label before pushing) and on demand.
- **Skipping.** A **draft** pull request, or one labelled **`skip-quality`**, stays on the light lane and CodeQL does not run on it: the long checks run when it is marked ready, or never when skipped on purpose. Build and tests always run.
- Only `develop` and `main` publish an image; a pull request never does. A push to `main` also runs semantic-release, which tags the version and the image with it ([ADR 0009](adr/0009-releases-are-decided-by-semantic-release-from-main.md)); `main` takes merge commits only.

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

The conformance lane and the Rust launcher of ADR 0014 are superseded by the .NET executor ([ADR 0017](adr/0017-orders-are-typed-tasks-approved-with-a-second-factor-that-the-executor-checks.md)): its tests are part of `dotnet test`.

On a machine with limited CPU, run heavy builds through the resource-limited slice (`systemd-run --user --slice=build-limit.slice --scope nice -n 15 …`) when it exists.

## 6. Before a vertical's pull request

Run the **`review-vertical`** skill: the five review agents run in parallel on the branch (the `spec-reviewer` checks that the README, the docs and the spec describe what is implemented), and their findings (and those of CodeQL and SonarQube) are triaged with the three-level protocol of ADR 0022 (`.claude/skills/review-triage/`): fixed, declined at medium or below with the reason, or, at blocker, critical, high or major, accepted by the maintainer in a comment; one table in the PR's *Review* section and `docs/review-decisions/<pr>.md`. An architecture verdict of "no" blocks the PR.

### Looking at it in a browser, yourself

This is **my own feedback loop**, used freely while building, not a gate at the end: a headless Chromium of the machine, with no tunnel and nothing asked of the maintainer. The maintainer's browser (the shared one) is for looking at something together, when they choose to open it. Once it is validated, the hand-over does not change: the address in the chat, and they look on their side.

Unit tests do not see what a person sees. Before a vertical goes to the maintainer, run it in the sandbox and **drive it in a real browser, the whole path of the vertical**: sign in, read the screen, click what a person clicks (a sort, a bubble, the bell, selecting a row and validating it), follow what happens next (a run that fills while the page stays open), and read the console. A first pass of this found, within minutes, a live channel that stayed closed after the sign-in, a text that named a column that no longer existed and a name cut too short; none of them had a failing test.

- **Playwright, headless Chromium, one browser and one page at a time** (a shared, small machine: check `uptime` and `free -m` first). Use any Playwright install of the machine from a script kept **outside the repository** (a scratch folder on disk, not `/tmp`): the script carries the address and the accounts of the sandbox, which are never committed.
- Print what matters as **text** (the rows, the badges, the states, the console errors) and take **one screenshot only when the look is the question**: read it, fix what it shows, then **delete it at once** (an image read stays in the context of every later turn).
- A bug found this way gets a test in the suite (Vitest or the API tests) in the same change, so that it stays found.

## 7. Seeing the code smells in the editor

The SonarQube rules can run in the editor (SonarLint) and be read by assistants, bound to the project of the SonarQube server.

- The binding is **local and never committed** (it holds the address of the server): `.sonarlint/connectedMode.json`, git-ignored.

  ```json
  { "sonarQubeUri": "https://sonarqube.homelab.example", "projectKey": "<owner>_<repository>" }
  ```

- Bind by the **address**, not by a connection id in `.vscode/settings.json`: an id that is not defined as a connection of the machine gives "The specified connection id doesn't exist", and the binding is invalid.
- The project is created on the server through its API (name, key `<owner>_<repository>`, main branch `develop`, the default Quality Gate), not by hand.
- **An assistant reads what the editor underlines** with the editor's diagnostics (the Problems panel, including SonarLint), before committing. SonarLint only analyses files that are open in the editor: editing a file opens it; wait about ten seconds, then read the diagnostics of the files changed, and fix them. This needs no SonarQube server for assistants.
- An assistant's SonarQube server that runs in a container reaches the editor's SonarLint on `localhost` only with the host network (`--network=host`); without it the editor is never found and the server falls back on a weaker analysis. One editor window per port: the port tells which window is analysed.
