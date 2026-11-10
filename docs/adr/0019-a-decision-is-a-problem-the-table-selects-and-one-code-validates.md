# 0019 — A decision is a problem; a table selects, one code validates, the description sets how far it goes

- **Status**: accepted by the maintainer on 2026-11-10; the screen was mocked up and agreed, and is built (first slice: dependency updates are the actionable rows; alerts, secrets, failing builds and images are shown with "no action defined", Q-30; production is a decision recorded with the approval, the executor validates and merges as a dry run).
- **Date**: 2026-11-10
- **Spec**: D-67 (refines D-15, D-16, D-21; supersedes the plan of ticked steps of ADR 0001 and ADR 0017 for the card)
- **Supersedes in part**: [ADR 0003](0003-work-items-are-per-repository.md) (the unit of decision)

## Context

The first card showed one work item per repository with a fixed plan (build and test, merge, deploy) to tick, and asked for a code per card. Tried in the sandbox, it failed: three dependency updates were one job; the plan was fixed before any analysis; details had to be unfolded to be read; one authenticator code per card made no sense. Dependency updates are the core of Orchestrarr, so the view must carry only what is useful to decide, and be pleasant.

## Decision

1. **A decision is about one problem**: one dependency update, one alert, one exposed secret, one backup finding. Each has a **category** and a plain description of the problem. A repository stays an attribute and the way to group the execution; it is no longer the unit of decision.
2. **A filterable table, one row per problem** (application, type, severity, state of the checks, what it will do), not a list ordered by my idea of urgency. Nothing unfolds: the row says what is useful, with a link to the detail.
3. **There are no step checkboxes.** What a row will do is computed from the application's description ([ADR 0020](0020-applications-are-described-in-a-private-configuration-repository.md)): a column "goes up to" (qualification, merge into `develop`, production). The configuration prevails over the action: the default is to go as far as the description allows, because the point is to automate.
4. **Selecting a row adds it to the list.** One switch per row, **"stop before production"**, off by default, keeps the release from being made. The whole selection is validated with **one authenticator code**, checked once by the executor for the batch.
5. **The execution is a batch per application** over the selected rows: build, test and qualify once, merge into `develop`, one promotion. The agent adapts: the chain stops at the first regression, and the ceiling is a limit, not an obligation to reach it.
6. **What the maintainer approves is the effective plan at the time of the approval**, including the version of the description it came from: a later change of the description does not change what was approved.
7. **The ceiling is the lowest of three things**: what the description says is possible, what the executor's rights list permits (ADR 0017), and the state of the problem. Production is only reached when the description declares a rollback and a check after the deployment; otherwise the chain stops before it, and the table says why.
8. **A row with no known action** (an alert without a pull request, an exposed secret) is shown with "no action defined" until the description says what to do ([SPEC Q-30](../SPEC.md)).

## What the first slice does

- **The ceiling** of a dependency update is the lowest of: the powers granted to Orchestrarr, what the connected executors accepted in their hello, the description of the application (how it is qualified, a rollback and a check after the deployment, the classes of update that may be promoted by themselves) and the state of the pull request (checks, mergeable). The reason it stops is part of the row.
- **One code, one batch.** Approving a selection builds one batch for all the applications chosen, so that the executor checks the code once (a code is good for one time step per operator). The executor will group by application when a real runner exists.
- **The client says only what it chose**: the row, the commit it saw, the stop switch. The server computes the plan again from the current table.

## Consequences

- The tables and the approval of ADR 0017 change shape: the batch is per application, the code is checked once, and the plan is derived, not ticked. The checks of the executor (code, rights, journal) stay.
- ADR 0003 is superseded for the unit of decision; its ids (`repository:` and `image:`) remain what an application is found by.
- A major update may stop earlier than a patch by default: the description says it per class of update ([SPEC Q-33](../SPEC.md)).
