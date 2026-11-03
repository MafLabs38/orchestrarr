# 0005 — Orchestrarr reads the live state of pull requests from the forge

- **Status**: proposed (needs the maintainer's decision on point 5)
- **Date**: 2026-11-03

## Context

Portarr says *which* repositories have dependency pull requests waiting (it counts them). To decide, and later to merge, Orchestrarr needs the live state of each one: what it updates and how far, whether its checks pass, whether the forge says it can be merged, which commit it is at. Portarr only gives a number (ADR 0002 keeps Portarr as the source of what is wrong; it never acts, its D-31).

## Decision (proposed)

1. **Portarr names the repositories with waiting pull requests; Orchestrarr reads their live state from the forge it will act on.** The forge is a provider behind an interface (`IPullRequestSource`), chosen by the host from the configuration, with a sample source for demos and tests.
2. **The read credential is read-only** (*Pull requests*, *Checks* and *Commit statuses*, read). It is not the credential that merges: a write credential exists only for one approved step, scoped to one repository (ADR 0001, ADR 0004).
3. **An approval binds to `(repository, number, head commit)`.** The head commit is part of the contract. A bot that rewrites its branch after the operator ticked is not merged unseen.
4. **"Ready to merge" is decided once, in Core** (not a draft, every check passes, the forge says clean) and sent as data, so that every client and the merge step use the same rule.
5. **Which accounts are "dependency bots" is a configuration of Orchestrarr today** (Dependabot and Renovate), while Portarr counts Dependabot's pull requests only. The two numbers can differ, and the screens say so (the forge returned none, or another number, or was cut at its limit). The clean solution, in the spirit of D-18, is that **Portarr counts every bot, or exposes the list**: it needs Portarr to change and the maintainer to decide.
6. **The forge is its own failure domain.** The pull requests are read after the report is built, in the background, with a bounded number at once and a time budget, never under the lock: a slow or limited forge cannot hide what Portarr says. A failure is said on the work item it concerns, with the last good list kept and marked.

## Consequences

- The cost of reading GitHub grows with the fleet: it is paced (one read per repository per five minutes unless asked, bounded parallelism, a rate limit has its own code). Conditional requests (ETags) and a GraphQL query are later optimizations.
- A repository kept by Renovate only gets no card today, since Portarr does not count Renovate's pull requests, until point 5 is decided.
- One forge and one token per instance for now; a repository's forge will come from the environment description when a second forge arrives.
