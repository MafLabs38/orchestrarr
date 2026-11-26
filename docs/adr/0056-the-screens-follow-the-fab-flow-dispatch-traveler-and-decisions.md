# 0056 — The screens follow the fab: the flow, the dispatch list, the traveler and the decisions

- **Status**: proposed (accepted when the pull requests that carry it are merged)
- **Date**: 2026-11-26
- **Spec**: D-110 ([SPEC.md](../SPEC.md)), FR-VIEW-1, FR-VIEW-3, FR-VIEW-7 and FR-VIEW-8 ([FUNCTIONAL.md](../FUNCTIONAL.md)). Builds on [ADR 0055](0055-the-flow-is-a-fab-lots-routes-machines-and-rules-that-keep-it-moving.md) (lots, routes, machines), [ADR 0053](0053-each-world-has-lanes-and-a-machine-is-never-idle-while-a-mission-waits.md) (lanes), [ADR 0052](0052-the-missions-screen-follows-problems-and-says-what-each-one-waits-for.md) (the Missions screen), [ADR 0047](0047-a-machine-has-a-state-and-leaves-a-reset-isolated-and-qualified.md) (machine states) and [ADR 0025](0025-everything-that-changes-is-pushed-to-the-screens-as-it-changes.md) (live by default). It supersedes the screens *Runs*, *Queue* and *History* of the first design ([ADR 0017](0017-orders-are-typed-tasks-approved-with-a-second-factor-that-the-executor-checks.md), [ADR 0029](0029-the-table-is-one-table-and-the-history-is-a-screen-of-its-own.md)), whose data the flow of missions no longer writes ([ADR 0038](0038-what-the-first-design-built-and-this-supersedes.md)).

## Context

The maintainer ticked the 19 rows of the demo at once and watched (chat of 2026-11-26). The core behaves: the route, the parking, the lanes and the fix-forward did their work. What he could not do is **see** it:

- *Runs*, *Queue* and *History* are empty: they belong to the typed tasks of the first design, which the flow of missions does not feed;
- *Decisions* still shows every row he had just validated, as if nothing had been decided;
- on *Missions*, a card is framed the same way whether it works, waits for a machine, waits for its turn on its repository or waits for its bot: "on ne sait pas pourquoi ils sont orange";
- nothing shows the workshops, their equipment, their capacity, what is in process and what waits in front of each.

His image is a clean-room fab followed by an MES: **the lots are the corrections to make, the workshops are the worlds** (staging with two machines, the forge with its slots, the release with its machine), the staging machines are the equipment; a qualification workshop will come one day. He asked for a flow that is **very graphic, live and limpid**, a dispatch list whose order he can change, a **hold** ("this work is held, the others pass, a complementary analysis is needed"), the steps of a lot one after the other, and decisions that leave the table once they are taken.

## What the field does (and what we take from it)

| Practice | Source | What we take |
|---|---|---|
| **A factory view**: WIP per workshop, the state of each piece of equipment, the priority lots, the bottlenecks, in one live screen | [Inficon Factory Dashboard](https://www.inficon.com/en/products/intelligent-manufacturing-systems/factory-dashboard), [Critical Manufacturing WIP Analyzer](https://www.criticalmanufacturing.com/mes-for-industry-4-0/apps/wip-analyzer/) (WIP split into queued, in process, processed) | the **Flow** screen: one column per workshop, its equipment with its state, what is in process on it and the buffer in front of it; the operations of each lot placed where they are |
| **Equipment states**: productive, standby, engineering, scheduled and unscheduled downtime (SEMI E10); running, idle, starved, blocked, faulted (line MES) | [SEMI E10 as read by the OPC Foundation](https://reference.opcfoundation.org/specs/OPC-40001-1/c-3), [Sepasoft equipment states](https://docs.sepasoft.com/articles/user-manual/configuring-equipment-states) | our machine states (ADR 0047) are shown in that vocabulary: **in process** (productive), **idle** (standby), **qualifying** (the restore and the checks: scheduled downtime), **down** (unscheduled downtime); a Git slot is idle or in process |
| **A dispatch list per workshop**: the queue in order, a "ready" column, filters, an order the supervisor changes | [Oracle MES dispatch list](https://docs.oracle.com/en/cloud/saas/supply-chain-and-manufacturing/25b/faumf/how-you-review-a-dispatch-list.html), [FabTime dispatch factors](https://www.inficon.com/en/products/intelligent-manufacturing-systems/fabtime-dispatch) (priority, current-tool FIFO) | the **Queue** screen: what waits, in the order it will go (priority, rank in the route, arrival, ADR 0055), why it waits, and two acts of the operator: **change the priority** and **hold / release** |
| **Hold and release**: a lot is held for an analysis, the others pass; a hold is a source of variability and its release is a decision (release, retest, scrap) | [FabTime on holds](https://inficon.com/media/11290/download/FabTimeNewsletter6.06.pdf), [the unexpected impact of lots on hold](https://semiengineering.com/the-unexpected-impact-of-lots-on-hold) | a held operation does not start and does not hold its lot: the next one of the route takes the turn; releasing it puts it back where the route says; the hold says who, when and why, and the screens show it as held |
| **The traveler**: the route of a lot, each step with its time and its outcome | MES lot tracking (the *traveler* of a lot) | the **History** screen: for each operation, its steps one after the other (validated, planned, queued, held, parked, each mission in each world, sent back, merged, released, closed or put aside), with times and reasons |

## Decision

1. **Four screens follow the fab, one job each, all live** (topics of `/api/v1/live`; polling alone is a defect, ADR 0025):
   - **Decisions**: the decisions **to take**, and below them the decisions **taken** (each row that was validated, with what became of it: in the line, in process in a world, held, done, put aside, and a link to its traveler; the rows set aside by the operator are there too and can be taken back). A row leaves the first table as soon as it is validated.
   - **Flow** (it replaces *Runs*): the factory view. One column per **workshop** (staging, forge, release; the decisions upstream, the outcomes downstream), and in each its **equipment** with its state and what it works on (the two staging machines, the slots of the forge, the deployment machine), the **buffer** in front of it and its counters; one row per **lot** (a repository) with its **operations placed in the column where they are**, each with its state in words and an icon. It reads like a map: at a glance, what moves, what waits and why, where the narrow place is. A click on an operation opens its traveler.
   - **Queue** (it replaces the queue of approvals): the **dispatch list**, the operations that wait, in the order they will go, with why each waits; the operator **changes the priority** of an operation (0 to 100, ADR 0050) and **holds or releases** it.
   - **History** (it replaces the history of approvals): the **traveler** of each operation, its steps one after the other.
   *Missions* stays, one card per problem (ADR 0052), and its frame says the state in words and colour: working, waiting for a machine, waiting for its turn, waiting for its bot, held.
2. **A hold is an act of the operator on an operation that has not started** (in the line, or parked for its bot): it does not start until it is released, and **it never holds its lot**: the next operation of the route takes the turn (ADR 0055 point 3). It records who, when and why (a reason in words is required), is written in the audit trail (`problem.held`, `problem.released`) and is shown everywhere the operation appears. Releasing it puts it back in the line where the route says. **An operation in process is not held**: what is running finishes its world (the global stop is there to stop everything). A hold is not a power: it can only delay work.
3. **The priority is set by a person** (ADR 0050 left it to the events): on an operation in the line or in process, from 0 to 100, recorded and audited (`problem.priority-set`). The line before a start is ordered by **priority, then rank in the route, then arrival** (ADR 0055 point 2, which the line did not apply to the priority yet: a waiting operation now carries its priority). A priority orders the work and grants no power.
4. **The screens read models made for them, computed from what the stores already hold**, never a second source of truth: the factory view (`GET /api/v1/flow/board`: workshops, equipment, lanes, buffers, lots and operations, and a few measures: work in process per workshop, operations done and put aside in the last hours, the age of the oldest waiting operation, the time a machine spent in process), the dispatch list (`GET /api/v1/flow/queue`), the traveler (`GET /api/v1/problems/{id}/traveler`, from the missions and the audit trail) and the decisions taken (`GET /api/v1/plan/taken`). They are published on the topics `missions` and `machines`.
5. **The vocabulary is the fab's in the screens** (ADR 0055 point 1): lot, operation, route, workshop, equipment, slot, buffer, line, hold; the Git world is shown as the **forge** workshop. An equipment state is said in a fab's words (in process, idle, qualifying, down) and an operation's state in words with an icon, never by its colour alone (Portarr DESIGN §5).

## What this does not do

- No hold of an operation in process, and no *future hold* (a hold set to take effect at a later step): they would stop a change half way through its worlds and keep its repository locked; they wait for a need.
- No change of the route by hand (the rank stays the rule of ADR 0055); the priority is the operator's lever.
- No qualification workshop yet: the screens take the workshops from the configuration of the worlds, so a fourth one appears when it exists.
- The measures are a few counters for reading the flow, not the statistics of a fab (utilisation per E10 class, cycle-time distributions): a screen of their own if a need comes.
- The backend of the first design (typed tasks, approvals of the Runner, ADR 0017) is not removed here: only its screens leave the menu. Removing it is ADR 0038's work.

## Defaults taken, for the maintainer to veto

- The names of the screens: **Decisions, Flow, Queue, History, Missions** (in French: Décisions, Flux, File, Historique, Missions).
- Decisions stays the home page (it is where the work starts); Flow is second in the menu.
- A hold needs a reason in words; a priority does not.
- The work is three pull requests, one after the other, each with its demo (the plan is in the maintainer's local documents): (1) the Flow screen and its read model, with the states of the Missions cards; (2) the Queue with priority and hold, and Decisions split into to take and taken; (3) the History (traveler) and the removal of the old screens from the menu.

## Consequences

- The maintainer sees the fab as he described it: workshops, equipment, lots, buffers and holds, live.
- The line gains two acts of the operator (priority and hold), in the starter of problems and the queue, which are launch contract files merged by the maintainer (rule 15).
- Three screens of the first design leave the menu; their endpoints stay until ADR 0038 removes their backend.
