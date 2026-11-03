# 0003 — Work items are per repository

- **Status**: proposed (needs the maintainer's confirmation)
- **Date**: 2026-11-03

## Context

The first vertical had to choose what one decision card is about. The spec said "findings of one application"; Portarr reports findings per repository and per running image, and an application is a looser notion: one application can be built from several repositories (a front and an API), and one repository can hold several applications.

The id of a work item becomes the key of approvals, the audit trail and the links in notification mails, so it must be frozen before anything persists against it.

## Decision (proposed)

1. A work item is about **one repository**, or **one third-party image** that is not built from a repository of the fleet.
2. Its id is `repository:<owner>/<name>` or `image:<reference>`.
3. The applications a repository serves stay an attribute (`appIds`), shown on the card, not a grouping.
4. A fork and an archived repository are not the operator's to maintain: they give no work item. SPEC Q-10 (an allow-list of repositories) will replace this rule.

## Consequences

- A fix, a merge and a build happen where the code is: the repository. The unit matches what a worker checks out and what a pull request touches.
- An application made of two repositories shows as two cards. Grouping them for one approval is a later, additive feature.
- A third-party image has no code to validate: its plan is a deployment, smoke tested and rolled back like any other (D-07, D-08).
- Changing the unit means a new ADR and a migration of every stored id.
