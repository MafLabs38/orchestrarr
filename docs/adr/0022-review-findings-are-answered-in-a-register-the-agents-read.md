# 0022 — Review findings are answered in a register that the agents read, and a serious finding that is not fixed is the maintainer's to accept

- **Status**: proposed (the maintainer described the protocol on 2026-11-05 and asked for it everywhere; accepted when this pull request is merged, which only the maintainer does: rule 15)
- **Date**: 2026-11-10

## Context

Five review agents read every pull request (and CodeQL and SonarQube read the code). They are made to find defects, so they always find something, they remember nothing from one round to the next, and they sometimes contradict each other. The answers of the author lived only in the pull request's description, which the agents do not read, so the same findings came back in every round (the documentation pull request of the launcher took close to forty), and the author was tempted either to follow every finding blindly or to set aside a serious one without anyone noticing.

## Decision

1. **Three answers, one table per pull request.**

   | Answer | When | Who decides |
   |---|---|---|
   | **fixed** | The finding is right | The author |
   | **declined** or **deferred**, rated medium, minor or low | A preference, a hardening beyond the threat model, a defect that does not occur, a duplicate: the reason is written | The author |
   | **declined** or **deferred**, rated **blocker, critical, high or major** (or a CodeQL security alert) | The author does not want to fix a serious finding | **The maintainer**, in a comment on the pull request, before it is final |

   The author is told nothing is needed for the first two rows, and tells the maintainer for the third only.
2. **A register in the repository**: `docs/review-decisions/<pr number>.md`, one entry per finding in a fixed shape ([the README of the folder](../review-decisions/README.md)). The agents read it ("Decisions already taken" in their instructions) and do not raise again a finding that an entry answers, unless the code changed so that the reason no longer holds. A serious entry without a valid approval is not trusted.
3. **A check, not only a promise.** The `review-decisions` job fails a pull request that adds or changes a serious entry that is not fixed and does not cite a comment of an approver (`.github/review-approvers`). It runs the checker and the list of approvers **of the base branch**: a pull request cannot approve itself.
4. **The rules that judge are in the maintainer's tier**: the folder, the checker, the approvers file, the job and the agents' instructions are launch contract files ([ADR 0015](0015-where-the-launcher-gets-what-its-checks-read.md) §3, rule 15).

## Consequences

- The agents' noise falls (answered findings are not repeated), and the author keeps the last word on everything that is not serious, so a round no longer chases a cosmetic point.
- A serious finding cannot be set aside silently: the maintainer sees each one, and the job proves it.
- The agents still judge differently from one run to the next, since they are models: the register reduces the noise, it does not remove it. It does not replace the author's check of each finding in the code.
- The protocol is product-independent: its common part (the checker, the job, the README of the register, the instruction for the agents, the user-level skill `review-triage`) lives in the organisation's template repository and is copied into each product; a product adds its own register and ADR.
- Setting the `review-decisions` job as a required check is a repository setting (`repos/orchestrarr.yaml` of the template repository), done by the maintainer.
