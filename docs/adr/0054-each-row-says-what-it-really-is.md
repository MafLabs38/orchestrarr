# 0054 — Each row says what it really is

- **Status**: proposed (accepted when the pull request that carries it is merged)
- **Date**: 2026-11-25
- **Spec**: D-108 ([SPEC.md](../SPEC.md)). Extends [ADR 0019](0019-a-decision-is-a-problem-the-table-selects-and-one-code-validates.md) (the table) and the plan of [ADR 0029](0029-the-table-is-one-table-and-the-history-is-a-screen-of-its-own.md).

## Context

The maintainer, auditing the demo (chat of 2026-11-25), could not tell what some rows were: *"Update actions/checkout 3 → 7"* means nothing to a person who has never met a pipeline tool. The **Type** column says how the row was found (*Update*, *Alert*, *Image*, *Tag*, *CI*), and a bot's pull request that raises a tool of the build pipeline, a library or the base image of a container are all an *Update*. He asked for **a column that says the real type, with an explanation of what the thing is**.

## Decision

1. **A row has a nature**, set by the control plane (`ProblemNature`, in the row of `GET /api/v1/problems`): *library*, *libraries* (a pull request that raises several), *pipeline tool*, *base image*, *security flaw*, *image behind*, *moving tag*, *pipeline fails*, or *other* (a row whose kind says it all). The nature also decides two things: the rank of the operation in its route ([ADR 0055](0055-the-flow-is-a-fab-lots-routes-machines-and-rules-that-keep-it-moving.md) point 2), and whether its missions may change the pipeline files (`PipelineChanges.MayChange`: a pipeline tool or a base image; a red pipeline by its kind). It is read by the control plane from the branch and the labels of the pull request, or from the kind and the location of the row, never from an agent's word.
2. **A pull request (a bot's or anyone's) says what it raises in its branch** (`dependabot/github_actions/…`, `dependabot/docker/…`, `dependabot/npm_and_yarn/multi-…`) **and in its labels** (`github_actions`, `docker`); one that says nothing of the sort is taken for a library, which is what a dependency bump is by default. Any other row is what its kind says, except an image row whose version is written in the `FROM` of a Dockerfile, which is the *base image* (`ProblemNatures.OfRow`, ADR 0055).
3. **The table has a column "What it is"**: the name of the nature, and under it **one sentence that says what it concerns**, the same for every row of a nature, in plain words. For the tools of a pipeline that a person meets most (`actions/checkout`, `actions/setup-node`, `actions/setup-python`) the sentence adds what that tool does. The column can be sorted.

## Consequences

- A person who does not know what a GitHub Action is learns it from the row, and sees that raising it never changes the code that is deployed.
- The words are in the interface (`en.json`), not in the data: another language changes the strings only.
- A bot that does not follow the branch or label conventions (Renovate's default branch names) is read as a library; a convention for it is added where it is met, not guessed.
- Nothing else of the table changes: kind, plan, why and the other columns are as they were.
