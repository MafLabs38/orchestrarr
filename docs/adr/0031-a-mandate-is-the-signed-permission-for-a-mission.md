# 0031 — A mandate is the signed permission to do a mission in a world

- **Status**: proposed (the architecture was described to the maintainer on 2026-11-13 and the maintainer agreed with it in broad lines; accepted when the pull request that carries it is merged)
- **Date**: 2026-11-13
- **Spec**: D-84 ([FUNCTIONAL.md](../FUNCTIONAL.md), [ARCHITECTURE.md](../ARCHITECTURE.md))

## Context

Moving between worlds needs a way to say what an agent may do, where, for how long. Today the approval of a decision card plays part of that role for fixed tasks. The maintainer wants the agent to **ask for a mandate** to enter the next world, and a second agent to grant it.

## Decision

1. A **mandate** is issued by the control plane for **one world and one mission**. A merge act names **the base branch and the head commit** it covers (SPEC Q-29, part 2). It states: the world and the place, the repositories and branches or the environment, the **acts allowed**, the frame (paths allowed and forbidden, network), the **budget**, the **expiry**, and who issued it.
2. It is **signed with an asymmetric key** whose private half is held **only by the control plane**; each runner holds the public half and **refuses** a mandate that is not valid, expired, revoked, or **issued for another runner or another world**. A mandate is **single-use for starting**: its nonce is recorded when a runner accepts it, and a runner that restarts resumes the **session** it recorded (a session id in its own store), within the mandate's validity, but cannot start it again. A send-back continues in the same session under the same mandate until it expires or its budget is used. Each **gated act** a mandate lists is **recorded when it is performed**, so that the same act is not performed twice by replaying the request and the runner checks the kill switch and revocation before every gated act. A key shared with the runners would let whoever takes a staging runner forge a release mandate.
   The **baseline mission** of ADR 0034 has no problem and no plan entry: its mandate is issued by the scheduler under a **system standing rule** that the maintainer created, limited to the staging world.
3. The **first mandate** of a problem (or the system rule above) comes from the plan (validated by the maintainer, or covered by a standing rule). The following ones come **only after the controller has crossed** the previous mission (ADR 0032).
4. A mandate **cannot be widened** by its holder; a change of scope is a new mandate. It **expires** and can be **revoked** by the kill switch, the maintainer or the budget.
5. It **reuses what exists**: a plan validation is an approval with the second factor, a standing rule is a delegation, the journal and the signature checks stay.
6. Every mandate, refusal and crossing is written to the audit trail.

## Consequences

- The approval, the delegation and the journal are adapted, not rebuilt.
- The checks that code can make are made by code (the plan covers the problem, the budget, the kill switch, the staging requirement, the diff touches no forbidden file, CI is green on the exact commit); the controller judges the rest.
