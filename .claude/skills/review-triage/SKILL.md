---
name: review-triage
description: Triage the findings of the review agents and of CodeQL/SonarQube on a pull request with the three-level protocol (fixed / declined minor / declined serious = the maintainer decides). Use whenever review reports, code-scanning alerts or Sonar issues come back on a PR, before presenting it, so as to keep control without chasing contradictory agents.
---

# Review triage: the three-level protocol

Review agents (and CodeQL, SonarQube) always find something, and they contradict each other from one round to the next. They advise; the author decides. This protocol keeps the author in control, shows the agents that a skip was deliberate, and makes sure the maintainer sees every **serious** finding that is *not* fixed. Decided by the maintainer on 2026-11-05; the mechanism is the `review-decisions` option of `MafLabs38/github-template` (`templates/quality/review-decisions/`); it works only if it is applied the same way everywhere.

## The three levels

| Level | When | Who decides |
|---|---|---|
| **A. Fixed** | The finding is right | The author (you) |
| **B. Declined / deferred, medium or below** (medium, minor, low; a test reviewer's remove/fix/add that is not right) | A preference, a hardening beyond the threat model, a defect that does not occur, a duplicate of a decision already written | The author, **with the reason written** |
| **C. Declined / deferred, serious** (blocker, critical, high, major; and any CodeQL "high"/security alert) | You do not want to fix a serious finding | **The maintainer**, before it is final |

Severity scales differ per agent: map them to blocker / critical / high / major (serious), medium, minor / low. CodeQL notes are low; its security alerts follow their level. A failing CodeQL check is never "declined by the author": either fix it, or (false positive) change the code so it no longer triggers, or go to level C.

## Procedure, after each round of reports (on the **head** commit only)

1. Read every report and every alert on the head commit. **Check each finding in the code** before accepting it (an agent can be wrong: say so in the entry).
2. Build **one table** (in the PR's *Review* section, or `docs/review-decisions/<pr>.md` when the register exists): id, agent, severity (normalised), where, decision (A / B / C), reason. Fixed ones get their commit.
3. Fix level A. Write the reason for level B in one line; do not spend a round on cosmetic points, and do not add detail to satisfy an agent (each detail gives it a new hold).
4. **Only level C interrupts the maintainer**: tell them which finding, what it would cost to fix, what you recommend. Wait for their answer in a comment on the PR; its link goes in the entry (`Approved`). Level A and B never need a message.
5. Push, label again (if the repo reruns agents on a label), wait for the reports on the new head **in the same turn**, repeat until only level B remains. Do not wait for an empty report.
6. Present the PR only when: the checks (CI, CodeQL, Sonar) are green on the head, the agents reviewed that head, and every serious finding is fixed or approved by the maintainer.

## What the agents do with the table

The agents read the register (`docs/review-decisions/`) and do not raise again a finding that an entry already answers, unless the code changed so that the reason no longer holds. A serious entry without the maintainer's approval link is not trusted: the agent raises it again. The register, the checker (`tools/review-decisions/check.py`), the approvers file and the agents' instructions are in the maintainer's tier: the author does not merge changes to them alone.

## Rules of thumb

- Never self-approve a level C. Never move a serious finding to B to avoid asking: if the agent rated it serious and you decline it, it is C.
- A false positive is still an entry: B with the proof (the code line, the test), so nobody investigates it twice.
- If a finding poses a real question of the maintainer (product, security, scope), ask it instead of settling it.
- The message to the maintainer is rare and short: what is ready, the demo, and only level C.
