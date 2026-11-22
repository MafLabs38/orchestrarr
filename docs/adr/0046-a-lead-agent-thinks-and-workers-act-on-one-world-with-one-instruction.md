# 0046 — A lead agent thinks and has no power; workers act on one world with one instruction

- **Status**: proposed (draft, local; accepted when the pull request that carries it is merged)
- **Date**: 2026-11-22
- **Spec**: D-100 (to be added with the code). Builds on [ADR 0032](0032-a-controller-agent-grants-the-next-mandate.md), [ADR 0031](0031-a-mandate-is-the-signed-permission-for-a-mission.md), [ADR 0035](0035-the-frame-is-enforced-by-the-environment.md) and [ADR 0045](0045-a-case-is-ticked-by-a-replayable-scenario.md).

## Context

The plan of a problem is fixed in code (staging, then Git, then release) and the controller only judges what a world hands over. Nothing reads the whole problem and decides what to do about it, so the complex cases of [USE-CASES.md](../USE-CASES.md) (many pull requests at once, a major upgrade, a database, several services) have no one to plan them. The reviews of the agents also keep finding the same thing: text written by someone else (a pull request's title, a message, a file) reaches an agent that holds a power.

**The maintainer decided this in the chat of 2026-11-22**: one agent in the control plane has the global view of the problem, only thinks, and has no right to act. The agents that act are started for a mission with a mandate: they have rights only on the world they work in, and only the instruction of their mission. "Without intelligence in the control plane there is no relevant workflow."

## Decision

1. **The lead agent** is the controller of ADR 0032, given the whole problem and one more job. It has no tool, no credential, no write path and a fresh context for each decision. It does two things, and both are **proposals in a fixed structure**:
   - **plan** (before the first mandate): which worlds, in which order, and for each one the instruction of its mission, and whether the problem is complex and why;
   - **judge** (after each handover): grant the next step, send back with remarks, end, or escalate to the maintainer (ADR 0032, unchanged).
2. **It reads the whole problem as data.** The problem as the control plane fetched it (kind, repository, the description with its source, the facts read by the control plane itself, the ceiling the maintainer approved) is given in the fenced section of the prompt, marked untrusted when it comes from a forge. What the lead writes back (an instruction, remarks) is text of an agent that read untrusted data, so it is bounded and is never a power.
3. **The conductor, which is code, decides what is allowed.** A plan is checked before anything starts: the worlds are a subsequence of staging, Git, release; none goes beyond the ceiling the maintainer approved; the instructions are short plain text with no marker; the budgets and the frame are the control plane's own. A plan that fails a check is refused, and the problem waits and is reported; it never runs on a default that nobody chose. The mandates are issued and signed by the control plane from the validated plan.
4. **A worker is started per mission, with one mandate.** It has the rights of one world and the instruction of one mission: it never receives the problem as a whole, the plan, the other worlds' instructions or the reasoning of the lead. What it hands over is a dossier, which is data for the lead.
5. **What this does and does not buy in security.** The only agent that reads the whole untrusted problem cannot act, and the agents that act read a short instruction and are held by mandates that the environment enforces (ADR 0035). A hostile title can therefore at most steer the plan among what the code allows. It does not make the lead immune to a manipulated plan, and a review may still flag untrusted text that reaches a model; what changes is the reach of such a flaw.

## Consequences

- The objective sentences built in code (`PlanEntryFactory`, `ObjectiveOf`) become the **default instruction** that the lead may refine within its length, and the check of the facts by the controller stays the control plane's own.
- Problems that are not pull requests become startable: the lead receives their description as data (the provenance question left open in [CASE-GRID.md](../CASE-GRID.md) is settled there).
- The lead's plan and its reasons are in the audit trail and shown on the Missions screen.
- A failure to run the lead, to read its answer or to validate its plan is never a grant: the problem waits and the maintainer is told after a bound (ADR 0036).
- The lead is an interim use of the maintainer's subscription like the controller (`Agents:Enabled`); its model and budget are configuration.
