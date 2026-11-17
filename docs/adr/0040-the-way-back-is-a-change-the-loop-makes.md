# 0040 — The way back is a change that the loop makes, through the same process

- **Status**: proposed (decided by the maintainer in the chat on 2026-11-17; accepted when the pull request that carries it is merged)
- **Date**: 2026-11-17
- **Spec**: D-93 ([SPEC.md](../SPEC.md), [FUNCTIONAL.md](../FUNCTIONAL.md) FR-WLD-13). Supersedes in part ADR 0001 point 3 and ADR 0036 points 2 and 4 (what the way back is). Closes Q-35, and Q-14 for the revert.

## Context

The first text of the restructuring gave production a way back of its own: a `Rollback` act, a revert held by the act gateway (with a deadline, a record of the base tree and a second check at the merge), a rule of admission ("the revert can land by itself") and, to be decided, an exception that would keep the rollback alive through the global stop and through an outage of the control plane (Q-35, Q-14). Fifteen findings of the review agents, repeated at every round, came from that single machine.

The maintainer's answer (2026-11-17): the repositories are delivered through their CI/CD pipelines, so production is reached through them; the Git flow is never bypassed; a release that goes wrong is rare, because it was tested before; and if it happens, a pull request on the branch is needed anyway, so there is always a merge to make. The idea is always to follow the process and to iterate with the agent until it solves the problem, as in a real development.

## Decision

1. **Production is reached only the way each repository is delivered** (its pipelines, GitOps, the declared way: ADR 0035 point 7, FR-WLD-11). No act exists to get around a rule of the Git flow: no bypass of a protected branch (no bypass actor, no administrator right, no force push, no direct push), for the merge identity or any other.
2. **The way back is not an act of its own.** A release that turns out wrong is a problem like any other (ADR 0036 point 1): a **new mandate** repairs it, through the same pull request, the same checks and the same approval as any change. **Reverting the change is the first repair tried when it is the quickest way, a fix is the second.** The agent iterates, through the process, until production is healthy again or the budget of the problem is used up and the maintainer is told (ADR 0036 point 3).
3. **A stop is a stop.** The global stop revokes every mandate and every act is refused; nothing is exempt. An outage of the control plane or of the approval authority changes nothing either: nothing acts until it is back, and Portarr and the mail tell the maintainer. What is wrong in production after a stop or an outage is a problem to open again, through the process.
4. **A merge that may deploy stays a release-world act** (FR-WLD-11a): under an approval that stops before it, it is refused, and each release needs the maintainer's code. This is unchanged; it is also the interim answer to Q-34, which the maintainer has not answered.
5. **What is removed**: the `Rollback` act; the revert held by the act gateway with its deadline, its record of the base tree, its second check at the merge and its mail; the admission fact "the revert can land by itself" (`RevertCanLandWithoutReview`, the refusal `RevertCouldNotLand`); the question of a bypass or of a waived review for a revert; the exception that the stop would keep for a rollback. **What stays**: `WayBackKnown`, now meaning that the previous version is known and can be restored through the process (data is Q-04).

## Consequences

- Production may stay on a wrong version for as long as a correcting change takes: a pull request, its checks and its approval. The maintainer accepts it, because a failed release is rare: it was proved before (staging, D-69; the Git world's pipelines, FR-WLD-14).
- ADR 0001 point 3 ("on condition that a rollback to the previous version is automatic") is **superseded in part**: what protects production is the proof made before, the verification after and the loop, not an instant automatic undo (SPEC D-08, D-09).
- The act gateway keeps one job on the forge: the merge, bound to the checked commit. A revert is a pull request like any other: the agent pushes it to its own `orchestrarr/*` branch, and the act gateway of the Git world opens and merges it when the mandate lists those acts (ADR 0033 point 3).
- A repository whose revert would need a human review is no longer refused a release for that reason: the revert waits for the review, as every change on that repository does.
- The safety model of rule 5 of `CLAUDE.md` is unchanged except for the words "deployment needs an automatic rollback", which this decision replaces in `CLAUDE.md`; that file is a launch contract file, so the maintainer merges the pull request that amends it (rule 15).
