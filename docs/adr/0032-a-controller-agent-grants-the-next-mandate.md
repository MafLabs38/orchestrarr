# 0032 — A controller agent reads the dossier and grants the next mandate

- **Status**: proposed (the architecture was described to the maintainer on 2026-11-13 and the maintainer agreed with it in broad lines; accepted when the pull request that carries it is merged)
- **Date**: 2026-11-13
- **Spec**: D-85 ([FUNCTIONAL.md](../FUNCTIONAL.md), [ARCHITECTURE.md](../ARCHITECTURE.md))

## Context

An agent that decides alone that its work is good cannot be the only judge. The maintainer asked for a second agent, **without bias**, that checks that the contract was respected before the work goes on, so that the two cross-check each other.

## Decision

1. The **controller** is an agent of the control plane, separate from the worker agent. It runs in its own sandbox with **no tools** and a **fresh context for every decision**.
2. It reads **the facts that the control plane fetched itself** (the diff and the pipelines' result from the forge on the exact commit, the health from Portarr) **and the dossier**, never the worker's reasoning. **The dossier and its evidence are written by the party being judged: they are untrusted data**, fenced as such in the prompt and never obeyed. A mismatch between the worker's claim and the facts fails a **mechanical check before the controller runs**, and a grant never covers a step that the facts fetched outside the agent do not support. The tests and captures that matter are made or digested by the runner.
3. Its decision is one of **grant**, **send back** (with precise remarks, the mission continues in its world), **end**, or **escalate** to the maintainer, always with reasons, in a fixed structure.
4. It **cannot widen the frame**, grant a world the plan does not allow, or waive a check that the code makes.
5. It is **bounded**: a number of send-backs per mission, after which the mission ends and the maintainer is told; repeated disagreement escalates.
6. It uses the maintainer's subscription like the worker agent.

## Consequences

- Independence rests on **inputs the worker did not write** (the facts fetched by the control plane, the runner's own evidence), not on the lack of tools alone. The worker and the controller use the same model, so their errors can be correlated: the mechanical checks come first.
- It adds a model run per crossing: budgets and the quota are watched.
- A decision can be wrong: the audit trail, the evidence and the maintainer's notifications are the net behind it.
