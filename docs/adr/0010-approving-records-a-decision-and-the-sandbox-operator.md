# 0010 — Approving records a decision; the order comes at run start; the sandbox operator never leaves the sandbox

- **Status**: proposed
- **Date**: 2026-11-03

## Context

The decision card has shown a disabled button since the first vertical. [ADR 0006](0006-a-launch-is-a-signed-order-and-credentials-are-short-lived.md) and [ADR 0007](0007-a-dedicated-vault-and-a-small-control-plane.md) say that a launch is a signed, short-lived order, and that no route may sign before the authentication question (SPEC Q-12) is settled. Making the button work is the first route that changes anything, so it has to be built without crossing either line, and without granting any power: there is no launcher yet, so nothing can run.

## Decision (proposed)

1. **Approving records a decision, not an order.** An approval is durable data: this repository, this pull request, **this commit** (SPEC D-37), these steps, this approver, this time. The signed order is issued later, when a run is about to start, because an order lives minutes and a run can wait for the workshop longer (ADR 0007). A bot that rewrites its branch after the tick changes the commit, and the old approval no longer covers it.
2. **The server decides everything but the ticks.** The client sends the work item and the steps it ticked. The repository, the pull requests, their commits, the powers and the approver are read or decided by the server from the current state; a request that carries other fields has them ignored. The approval is refused unless the pull requests were read (not still pending, not an old read), the kill switch is off, the approver is a person with the operator role, every ticked step is offered, **granted** and recorded by this vertical (building and testing a pull request, and merging those that pass), and a merge is never approved without its validation.
3. **Powers are granted in the configuration** (`Powers:Grants`: a repository or `*`, and steps), off until listed (SPEC D-10, D-11). A grant covers steps of a repository; a step on an environment (a deployment) is granted per environment, which the vertical that deploys will do, so it is never granted here.
4. **Fail closed, and traced first.** An approval whose trace cannot be written is refused. The trail records who approved what (commit and steps included) and every refusal with its code, and never a field that has not the shape of an order's. The global stop works even when its own trace cannot be written.
5. **The operator is a sandbox operator until the sign-in exists.** `Approvals:Mode` is `Disabled` by default: nobody can approve and the interface says so. `Development` gives a single fixed operator with no sign-in, **and the host refuses to start with it outside the Development environment**, so it cannot reach a real deployment by a configuration mistake. The sign-in through the identity provider (SPEC D-40) replaces it and is the next thing before anything acts in production.
6. **The stores are in memory and bounded**, and say so: a full queue or a full trail refuses instead of forgetting. The persistent trail comes with the workshop (SPEC D-43).

## Consequences

- The interface can show the whole round trip (tick, approve, queue, trail, stop) while nothing runs; the Queue says no launcher exists yet.
- Restarting the sandbox forgets the queue and the trail, which is accepted for a sandbox and refused for production by the mode.
- An approval records what was seen, so a plan that is stale refuses rather than approving something else.

## Not decided here

- The sign-in (SPEC Q-12) and who counts as an operator in the identity provider.
- Whether one approval can cover a pull request that is updated later by the bot (it cannot today: the commit binds it).
