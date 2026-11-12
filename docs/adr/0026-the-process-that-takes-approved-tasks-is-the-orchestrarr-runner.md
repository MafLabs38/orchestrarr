# 0026 — The process that takes approved tasks is the Orchestrarr Runner

- **Status**: proposed (asked for by the maintainer on 2026-11-12; accepted when the pull request that carries it is merged)
- **Date**: 2026-11-12
- **Spec**: D-74 (renames the "executor" of D-65 and ADR 0017)

## Context

ADR 0017 called the .NET process that takes approved batches, checks them and runs their tasks the **executor**. The word does not describe what it is, and in the neighbouring tools it means something else: in GitLab CI an *executor* is the method a runner uses to run a job (shell, docker, kubernetes), in Jenkins a slot of work on a node, in Airflow the strategy of the scheduler. What this process does is what a **runner** does in GitHub Actions and GitLab CI: it takes approved work and runs it in containers. "Agent" is taken: in this product the agent is the AI (Claude Code) that tries a repair.

## Decision

1. The process is the **Orchestrarr Runner**, "the Runner" in prose. The word *runner* alone is never the name of the product, because it brings a CI/CD system to mind: it is always "Orchestrarr Runner" (the Runner after the first mention).
2. Everything renames with it: the project (`Orchestrarr.Runner`), the configuration section and the variables (`Runner:`, `ORCHESTRARR_Runner__…`, `Tasks:Runners`), the routes (`/api/v1/runner/…`), the columns of the screens, the documents and the tests. There is no alias: the product has not been released.
3. The task handlers that were called "runners" inside the executor (`ITaskRunner`) become **task handlers** (`ITaskHandler`), so that "the Runner runs its handlers" reads without a double meaning.
4. The column of the storage that names the process that took a batch is renamed by a migration (`batches.executor` becomes `batches.runner`, schema version 5).
5. The columns of the table of decisions say **CI** (the pipeline of the pull request, which was "Forge") and **Runner** (what the Orchestrarr Runner last did on that commit, which was "Workshop"); the word *workshop* leaves the interface. Internal names stay for now: the field `workshop` of the API, `WorkshopVerdict` and the option `Validation:Workshop`, which declares that a host may run the code of pull requests (ADR 0024).
6. The earlier ADRs keep their text and their file names as the record of what was decided at the time; each carries a line that says that "executor" is now the Runner.

## Consequences

- The pull request that renames touches launch contract files (rule 15 of `CLAUDE.md`): the maintainer merges it.
- The database of the control plane is migrated by the schema step. The Runner's own store (`executor.db`, which holds the enrolments of the operators and the codes already used) is moved to `runner.db` at the Runner's start when only the old file exists next to the new default path; a path set by hand is the operator's.
- The old names are never read silently. The host refuses to start while `Tasks:Executors` is set, and the Runner refuses to start while `Executor` settings, `ORCHESTRARR_Executor__…` variables or an `executor.json` exist: a Runner that started with the defaults would have no rights and no token, and nothing would say why.
