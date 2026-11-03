# 0008 — A fifth review agent keeps the documentation true

- **Status**: proposed
- **Date**: 2026-11-03

## Context

The four review agents (tests, quality, security, architecture) look at a change. None of them asks whether the **documents still describe the product**. In a project driven by a specification, that is where the damage accumulates: a decision marked open that the code settled, a command in the README that no longer exists, a statement that "no write power exists" that stopped being true. The maintainer has seen the specification and the implementation drift apart over time, and the drift is silent: each change is reasonable, and nobody reads the whole.

The quality reviewer checks compliance of the **code** with the spec, one direction, on the diff. Drift is mostly the other direction (the code moved, the document did not) and mostly outside the diff (a file nobody touched).

## Decision (proposed)

1. **A fifth agent, `spec-reviewer`**, reviews the **documentation against the code**, in both directions: what the documents say that the code no longer does, and what the code does that no document says. It reads the README (the entry point) first, as a newcomer would, then the docs, the spec (statuses, identifiers, roadmap, changelog, version), the ADRs, the samples and the configuration they describe.
2. **It sweeps the whole documentation, not only the diff**, because drift lives in files a change did not touch; the diff tells it where to look first.
3. **Every finding carries evidence on both sides and the exact text to write**, so the fix is mechanical and can be applied in the same pull request. It never edits: like the others it is read-only and advisory in CI; the `review-vertical` skill triages and applies its findings locally.
4. **It runs where the others run**: locally through the `review-vertical` skill before a vertical's pull request, and in CI on pull requests when they are opened or marked ready, and again with the `ai-review` label, not on drafts, forks or `skip-quality` (Sonnet, on the maintainer's subscription). The Review table of the pull request gets a row for it.
5. **A statement about powers, safety or stage that is false is a blocker**, because these documents are the safety model's source of truth (CLAUDE.md, the spec's §7).

## Not decided here

- Whether the agent should also open the documentation pull request itself once the product can act (it has no write power, like the rest, until the maintainer decides).
- Whether Portarr and Mnemarr adopt it; the agent is written for this repository's documents, and its principles carry over.

## Consequences

- A fifth comment on each pull request and a little more of the subscription quota; the same cost class as the quality reviewer.
- A documentation fix is part of the change it belongs to, not a later chore. A change that alters behavior and moves no document is reported.
- The pipeline is no longer identical to Portarr's (four agents); CLAUDE.md says so.
