---
name: review-vertical
description: Run the five review agents (tests, quality, spec, security, architecture) in parallel on the current branch before opening or updating a vertical PR, then triage and apply their findings.
---

# Review a vertical before its PR

1. Base: `develop` (or the given base). Make sure the branch is committed and the full test suite is green (`dotnet test --solution Orchestrarr.slnx`, `yarn test` in `frontend/`).
2. Launch **in parallel**, each with the base and the branch name, asking for its Markdown report:
   - `test-reviewer`: relevance, redundancy, missing cases, speed, safety behaviours;
   - `quality-reviewer`: spec compliance, design, smells, frontend conventions;
   - `spec-reviewer`: the documentation against the code, in both directions (README, docs, spec, ADRs); it gives the exact text to write;
   - `security-reviewer`: OWASP review and the safety model;
   - `architecture-reviewer`: technical lead, constellation, layers, hard-coded versus configurable, decisions.
   If the agents are not registered in the session, spawn general-purpose agents with the content of `.claude/agents/<name>.md` as instructions.
3. **Triage** the five reports (and the CodeQL and SonarQube findings) with the protocol of `.claude/skills/review-triage/` (ADR 0022): fix what is right; decline medium and below with the reason in one line; a blocker, critical, high or major finding that you would not fix is the maintainer's to accept, so tell them and wait for their comment. Do not accept a finding without checking it in the code. An **architecture verdict of "no" blocks the PR** until it is addressed or decided by the maintainer (written in the PR's Review section).
4. Re-run the tests and note the durations before and after.
5. Add a **Review** section to the PR description (and the entries of `docs/review-decisions/<pr>.md`): one table, per finding the agent, the normalised severity, the answer and the reason; test count and duration before and after.
