# 0001 — Orchestrarr merges and deploys on the operator's approval

- **Status**: accepted
- **Date**: 2026-11-01
- **Amends**: the initial safety principles ("a human merges", "approval gates beyond a pull request") of `docs/VISION.md` §5 and rule 5 of `CLAUDE.md`

## Context

The operator runs more and more applications. Dependency pull requests pile up unvalidated because validating, merging, deploying and checking each one by hand costs more time than the operator has. The operator rarely reads the code; they check that the AI did not drift, which is visible quickly.

An orchestrator that only opens pull requests leaves the expensive part (merge, deploy, verify) to the human, so the backlog stays.

## Decision

1. **No autonomy of decision.** Orchestrarr never starts a work item on its own. Every work item starts from an explicit operator approval on a decision card.
2. **Autonomy of execution.** Once approved, Orchestrarr **merges and deploys itself**, up to the steps the operator left ticked on the card. The operator's tick is the human approval.
3. Deployment goes **directly to the target environment** (no pre-production), on condition that a rollback to the previous version is automatic.
4. The other safety principles stay: isolation, least privilege, verify then report, budgets and kill switch, audit trail, untrusted input.

## Consequences

- Orchestrarr holds credentials that can merge and deploy. They are scoped to one repository or one target, and live only for the duration of one approved step.
- Powers are granted per application and per environment and are off until enabled; automatic deployment requires a known rollback method.
- Orchestrarr's identity may approve and merge its own pull requests (SPEC D-19). The forge review is therefore not relied upon as a safety net: the operator's approval on the card, the verification and the rollback are. Rulesets still forbid direct pushes and force pushes to protected branches, and limit who can bypass them.
- Changing this requires a new ADR that supersedes this one.
