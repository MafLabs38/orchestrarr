<!-- The title is a Conventional Commit (feat(scope): …, fix(scope): …, chore(scope): …). -->
## Summary
<!-- Briefly describe your changes (1-3 bullet points) -->

-

Closes #

## Type of change
- [ ] Vertical (a feature end to end, see the roadmap in the spec)
- [ ] Bug fix (non-breaking change which fixes an issue)
- [ ] Breaking change (fix or feature that would cause existing functionality to not work as expected)
- [ ] Refactoring (no functional changes)
- [ ] Documentation or pipeline

## Spec references
<!-- Features, decisions or questions covered: F-XXX-nn, D-xx, Q-xx. A structural decision has its ADR. -->
-

## How has this been tested?
- [ ] Unit tests (backend and/or frontend)
- [ ] API tests
- [ ] Manual testing in the sandbox, and accepted by the maintainer

Known limitations:
-

## Safety
- [ ] No power was added that merges, deploys or writes to a repository, or the maintainer decided it (ADR 0001)
- [ ] No real infrastructure value, no secret (public repository)

## Version
The title of this pull request is a Conventional Commit and, once squash-merged into `develop`, decides the version of the next release (`feat` minor, `fix` patch): check that its type is the right one.

## Review
<!-- Filled by the review-vertical skill: per agent (tests, quality, security, architecture), what was fixed and what was
     declined with its reason; test count and duration before and after. An architecture verdict of "no" blocks the PR. -->

| Agent | Fixed | Declined (and why) |
|---|---|---|
| test-reviewer | | |
| quality-reviewer | | |
| security-reviewer | | |
| architecture-reviewer | | |

## Checklist
- [ ] I performed a self-review
- [ ] Tests pass locally (`dotnet test --solution Orchestrarr.slnx`, `yarn test` in `frontend/`)
- [ ] Lint is clean (`dotnet format --verify-no-changes`, `yarn lint`, `yarn stylelint`, `yarn format:check`, `yarn typecheck`)
- [ ] Documentation is updated
