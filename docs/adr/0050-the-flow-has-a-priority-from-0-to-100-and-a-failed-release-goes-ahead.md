# 0050 — The flow has a priority from 0 to 100, and a release that failed goes ahead of the others

- **Status**: proposed (accepted when the pull request that carries it is merged)
- **Date**: 2026-11-24
- **Amended by**: [ADR 0058](0058-a-release-that-fails-after-its-merge-is-fixed-forward.md): a release that did not verify is fixed forward in staging (point 4), and escalated when the application has no staging
- **Amended by**: [ADR 0056](0056-the-screens-follow-the-fab-flow-dispatch-traveler-and-decisions.md): a person sets the priority of an operation in the line or in process, on the Queue screen
- **Spec**: D-104 ([SPEC.md](../SPEC.md)). Extends [ADR 0049](0049-problems-on-one-repository-are-done-one-at-a-time-and-a-conflict-is-not-mended-here.md) (the line of one repository), [ADR 0040](0040-the-way-back-is-a-change-the-loop-makes.md) (a failed release is a failed push: the loop goes on with the same problem) and [ADR 0047](0047-a-machine-has-a-state-and-leaves-a-reset-isolated-and-qualified.md) (a machine is leased to one mission). Amends FR-WLD-13 and FR-MIS-6 ([FUNCTIONAL.md](../FUNCTIONAL.md)): a failed release no longer blocks anything by a rule of its own; it goes ahead.

## Context

ADR 0049 left one question to the maintainer: when a problem is escalated, does it free its repository (what the line does) or keep blocking the releases of its application (FR-WLD-13, proposed, never built)? His answer, in the chat of 2026-11-24, is that **neither is the right question**: it is a matter of **flow**, not of a special rule. The product is a line of lots going through stations (the worlds), and it should be managed as a fab manages its flow:

- every station has **its own queue and its own constraints**, sequential where it has to be: the staging machine and the release machine take one lot at a time, and the two machines never work on the same repository at once (two pull requests would collide);
- everything else is a list of work with **priorities**. A release that is out and breaks production is a lot to **rework**, and a lot that is urgent goes ahead of the others as soon as the machine is free; it does not stop the others by a rule, and it is not stopped itself to wait for a person;
- a lot that is on a machine finishes its step: nothing is pulled out of a machine in the middle of it.

He also asked that the priority be **a number**, not a few names: *"normal is 50, an urgent starts at 70, then it goes up"*, so that the flow can be told apart more finely later with the same mechanism.

## Decision

1. **A problem has a priority, a number from 0 to 100** (`FlowPriority`). **50 is normal** (every problem a person validates). An urgent event sets it to **70** at first and adds **one** each time it happens again, up to 100.
2. **The priority orders the next mission a runner is given**: among the missions that could be offered, the more urgent problem first, then the later world (what is on its way to production is finished before something new starts), then the identifier. A machine is leased to one mission at a time (ADR 0047), so this is what puts an urgent lot on the next free machine. **Priority goes before the world**: an urgent staging mission is offered before a normal release on its way to production, which reverses the earlier "finish what is on its way" rule for normal against urgent work; between equal priorities the later world still goes first. A mission whose problem is unknown goes last.
   - **The line of ADR 0049 is not ordered by it**: a problem enters it at normal priority and waits in the order of the route (ADR 0055). When a source of urgency can reach a problem before it starts (FR-FIND-6, a serious advisory), the line can use the number; it is not built.
   - **The turn of a repository** (which of its problems in progress may be offered a mission) does **not** use it either: it stays the oldest first. The priority changes while a problem runs, and the turn must not move under a change that is half way to production.
3. **It orders; it never grants.** The priority is not part of the approval or of any mandate, widens no power, skips no check and no world, and **never interrupts a mission that runs**: a lot on a machine ends its step, and the urgent one is next.
4. **What raises it, for now: a release that did not verify.** The mission ends, the problem goes back to the first world (ADR 0040, the same problem, the same approval, no other validation) and **its priority is raised one step, in the trail** (`problem.priority-raised`, with the old and the new number). Production is touched, so the rework goes ahead of the normal lots for the next free staging. It holds its repository until it is closed, so the problems that wait behind it on that repository wait for it, which is the rule of ADR 0049 and not a new one.
5. **FR-WLD-13's rule that an escalated problem keeps blocking its application's releases is withdrawn.** What it wanted (no other release lands on a production that is known to be failing) is given by the flow **only while the loop goes on**: the problem is in progress, holds its repository and goes first, so no other problem of that repository starts or releases before it is closed. **When the loop reaches its bound the problem ends as failed, the maintainer is told, and the repository is free**: the next problem of the line can start, and release, on a production that is still known to be failing. The same holds for a problem escalated for another reason, and for a release under a standing rule that is escalated to a person (ADR 0049). Whether a repository stays blocked after a repair gave up, until the maintainer answers, is **his to decide** and is not built (see below).
6. **The queues of the worlds and their constraints** are, in one table:

| World | Resource | Constraint | Where it is kept |
|---|---|---|---|
| Staging | the staging machine | one mission on the machine (a lease) | the lease of the machine (ADR 0047) and the order of the missions offered (this ADR) |
| Git | none | one problem per repository, because each merge moves the base | the line of the repository (ADR 0049) |
| Release | the release machine, the environment | one mission on the machine; one problem per repository | the lease, the line, the order offered |

   The two machines never work on the same repository at once because a repository has one problem in progress. A queue of its own for the Git world is not needed: the act gateway merges at once, under the line of the repository.

## What this holds only on the test bed

The **line** is kept in memory, like the other stores of the test bed: a restart empties it and the maintainer validates again. The **priority** is kept with the problem (`ProblemRun`, not the plan entry that the approval covers) in the problem store: it survives a restart wherever that store does. It is not shown on a screen (what the maintainer sees is his to choose, rule 16 of `CLAUDE.md`): it is in the trail (`problem.priority-raised`, written when the new mandate is issued) and in `problemPriority` of the missions list of the API.

## Defaults taken, for the maintainer to veto

He did not answer these four points in the chat; they are the defaults of this change:

- **Only a release that failed is urgent.** A critical security alert is not made urgent by this change (FR-FIND-6 says a serious vulnerability goes first; it is the source of findings that sets the number when one exists, which is later).
- **A problem holds its repository until it is closed**, merged and deployed included, as FR-MIS-6 already says.
- **No pre-emption**: an urgent problem never takes a machine from a mission that runs.
- **A repository is not kept blocked after a repair gave up** (the loop's bound): it is free, as after any failure, which is what he first asked for. The alternative is a hold on that repository until he answers; it is a small change in `ProblemStarter.Busy` and it is his decision.

## Not decided here, not built

- **A person can set the priority** of a problem when he validates it: not built (he did not ask for it, rule 16).
- **A problem escalated after a release under a standing rule**: nobody approved it, so a person decides (FR-WLD-13, kept). When he answers and the loop goes on, it should come back at its raised priority; it is not built, because the product has no standing rule on a deployment yet.
- **Rules finer than a number** (aging a normal problem that waits too long, a ceiling per kind): possible later with the same number. Nothing ages a normal problem today, so urgent problems that keep arriving could starve it; acceptable on the test bed, to settle before a real fleet.
