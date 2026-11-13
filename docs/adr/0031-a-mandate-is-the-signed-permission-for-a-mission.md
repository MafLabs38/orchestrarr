# 0031 — A mandate is the signed permission to do a mission in a world

- **Status**: proposed (the architecture was described to the maintainer on 2026-11-13 and he agreed with it in broad lines; accepted when the pull request that carries it is merged)
- **Date**: 2026-11-13
- **Spec**: D-84 ([FUNCTIONAL.md](../FUNCTIONAL.md), [ARCHITECTURE.md](../ARCHITECTURE.md))

## Context

Moving between worlds needs a way to say what an agent may do, where, for how long. Today the approval of a decision card plays part of that role for fixed tasks. The maintainer wants the agent to **ask for a mandate** to enter the next world, and a second agent to grant it.

## Decision

1. A **mandate** is issued by the control plane for **one world and one mission**. It states: the world and the place, the repositories and branches or the environment, the **acts allowed**, the frame (paths allowed and forbidden, network), the **budget**, the **expiry**, and who issued it.
2. It is **signed** by the control plane; the runner **verifies the signature and refuses** a mandate that is not valid, expired or revoked. The choice of signature (a shared key or an asymmetric pair) is made when it is built.
3. The **first mandate** of a problem comes from the plan (validated by the maintainer, or covered by a standing rule). The following ones come **only after the controller has crossed** the previous mission (ADR 0032).
4. A mandate **cannot be widened** by its holder; a change of scope is a new mandate. It **expires** and can be **revoked** by the kill switch, the maintainer or the budget.
5. It **reuses what exists**: a plan validation is an approval with the second factor, a standing rule is a delegation, the journal and the signature checks stay.
6. Every mandate, refusal and crossing is written to the audit trail.

## Consequences

- The approval, the delegation and the journal are adapted, not rebuilt.
- The checks that code can make are made by code (the plan covers the problem, the budget, the kill switch, the staging requirement, the diff touches no forbidden file, CI is green on the exact commit); the controller judges the rest.
