# Orchestrarr — Testing and quality

> Same strategy as Portarr's ([TESTING.md](https://github.com/MafLabs38/portarr/blob/develop/docs/TESTING.md)), scaled to what exists. The three products share conventions.

## 1. What is tested, and where

| Level | Tool | What it proves |
|---|---|---|
| Backend unit | xUnit v3, AwesomeAssertions, NSubstitute, `FakeTimeProvider` | The domain (work items, plans, the cached catalog) and the providers (mapping a source's report to the fleet model) |
| API | `WebApplicationFactory` | The REST contract: routes, JSON shape, 404, that no route runs anything and the two that record something are off unless the sandbox operator is configured or a person signed in; with the sign-in on, the probe `GET /health` and the security headers are checked, and a session is given to the test in a header (the provider is not reached), and the redirect to the provider is checked with its configuration given |
| Launch rules (conformance) | The vectors in `contract/launch/`, run by the C# tests and by the Rust tests (**the launcher and the vectors are not built yet**) | That the two implementations of the launch rules agree, byte for byte on the canonical form and case by case on the decision: one vector per refusal, the accepted order, and the awkward ones. A change to the rules changes both implementations and the files in one pull request ([ADR 0014](adr/0014-the-launcher-is-a-rust-binary-held-to-the-control-plane-by-shared-vectors.md)) |
| Storage | A real SQLite file in a temporary folder | What must survive a restart: persistence after reopening, the unique key of a decision, all-or-nothing batches, the append-only trail, the capacities, and the global stop (a new database is released, an unreadable one is engaged, a release that cannot be saved is refused) |
| Frontend | Vitest, Testing Library (`happy-dom`) | The screens and components: what is shown, what is ticked, what is disabled and why |
| End to end | Playwright | Not yet. It arrives with the first vertical that has a write path |
| Acceptance | The rotten test repository (`SCENARIOS.md` of `orchestrarr-testbed`) | Find, fix, merge, deploy and roll back on a real application |

Rules: a test protects a behavior someone relies on, runs in milliseconds, and fails for one reason. Time is a `FakeTimeProvider`, never a sleep. **Safety behaviours are tested like features**: while no launcher exists, the tests prove that no route runs anything and that the two that record something answer only to an operator (the sandbox operator on the loopback, or a person of the operator group who signed in, from the application's own origin); a step that cannot be done is unavailable and unticked; a power that is off is not offered.

## 2. Other automated checks

| Check | Tool | When |
|---|---|---|
| Backend lint | .NET analyzers and the Sonar C# analyzer (the SonarLint and SonarQube rules; warnings are errors), `dotnet format --verify-no-changes` | PR |
| Launcher lint (once it exists) | `cargo fmt --check`, `cargo clippy` with warnings as errors, an audit of the dependencies | PR |
| Frontend lint | ESLint (strict, SonarJS, a11y, import order), Stylelint, Prettier, `tsc --noEmit` | PR |
| Quality and coverage | SonarQube Quality Gate | full lane |
| Static security analysis | CodeQL (C#, TypeScript, workflows), `security-and-quality` queries | PR (skippable) and weekly |
| Secrets | gitleaks | PR |
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

The launcher's commands (`cargo test`, `cargo fmt --check`, `cargo clippy`) come with the launcher; the vectors are regenerated by a C# test that fails when the committed files differ, and read by both suites; each suite fails on a vector file it did not read, every refusal code has at least one vector (a test fails when one has none), the signature vectors run against the real verifier of each side (the decision vectors script it), the files are written deterministically (sorted keys, LF, invariant culture), the dry-run launcher is composed with no adapter to Vault, a forge or a container runtime, and the generator fails before writing anything if the C# launcher disagrees with the outcome declared by hand for a case.

On a machine with limited CPU, run heavy builds through the resource-limited slice (`systemd-run --user --slice=build-limit.slice --scope nice -n 15 …`) when it exists.

## 6. Before a vertical's pull request

Run the **`review-vertical`** skill: the five review agents run in parallel on the branch (the `spec-reviewer` checks that the README, the docs and the spec describe what is implemented), and their findings are triaged (fixed, or declined with the reason) in the PR's *Review* section. An architecture verdict of "no" blocks the PR.

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
