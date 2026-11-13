# 0037 — The maintainer is told by mail when it matters, and a complex case asks to be tried

- **Status**: proposed (the architecture was described to the maintainer on 2026-11-13 and the maintainer agreed with it in broad lines; accepted when the pull request that carries it is merged)
- **Date**: 2026-11-13
- **Spec**: D-90 ([FUNCTIONAL.md](../FUNCTIONAL.md), [ARCHITECTURE.md](../ARCHITECTURE.md))

## Context

The maintainer wants to touch nothing in routine, to be told when something needs him, and to try the application after a complex change.

## Decision

1. The **worker agent decides** that a case is complex and **says why**.
2. A complex case, when done, sends **an email**: the problem, what was done, why it is complex, **what to test**, a link to the dossier.
3. The maintainer is also notified **at once** (by mail) of a serious vulnerability, an exposed secret, a mission that failed or was stopped, a loop that hit its limit, the kill switch, and a staging that has just been found broken (the **event**); the **lasting state** of a broken staging is shown in the bell.
4. A simple success sends nothing; it is in the history.
5. Notices about the description (unreadable repositories, a broken staging) go to the notification bell, never banners (CLAUDE.md rule 16).
6. The mail is composed by the control plane: the agent's text is quoted as plain text and the only link is the product's own; agent-written content shown in the interface is plain text or served from a separate origin with a sandboxing policy, never executed.
7. The mail path (through Portarr or directly) is chosen when it is built.

## Consequences

- A notifier becomes a component of the control plane.
