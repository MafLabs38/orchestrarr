# 0052 — The Missions screen follows problems, and says what each one does or waits for

- **Status**: proposed (accepted when the pull request that carries it is merged)
- **Date**: 2026-11-25
- **Spec**: D-106 ([SPEC.md](../SPEC.md)). Serves FR-VIEW-2 ([FUNCTIONAL.md](../FUNCTIONAL.md): the missions in progress, live, with what the agent is doing). Builds on [ADR 0025](0025-everything-that-changes-is-pushed-to-the-screens-as-it-changes.md) (live by default), [ADR 0049](0049-problems-on-one-repository-are-done-one-at-a-time-and-a-conflict-is-not-mended-here.md) (the line of a repository) and [ADR 0050](0050-the-flow-has-a-priority-from-0-to-100-and-a-failed-release-goes-ahead.md) (the priority).

## Context

The maintainer, 2026-11-25, before a demo: the product has to be *followed and managed from its interface*, not only to repair correctly. He must understand **what happens and where**, whether the flow of a correction is **blocked or still active**, and the screens must react **in real time** and be **responsive**. "It is no use showing an application that is on the floor from the interface side."

A real run, watched in a browser (text, a screenshot read once and deleted), found what stood in the way on the Missions screen: the missions of every problem in **one flat list** (50 of them, newest first, the same objective sentence on each), so that a person could not tell which problem a mission belonged to; a card that **jumped** to the top each time something moved; nothing for a problem that **waits in the line** of its repository; no way to tell a mission that works from one that waits for a runner that is busy elsewhere or for a machine that is not free; an age that never moved when nothing was said; and, on the Decisions screen, the row that had just been validated **did not say it was being worked on**.

## Decision

1. **The Missions screen is a list of problems**, one card each: what it is (what the control plane read of the row), its repository, its state (*in line*, *working*, *waits for you*, *done*, *failed*), its priority when it is urgent (70 and above, ADR 0050), the worlds it goes through with where it stands in each (not started, waiting, running, the controller reading, done, ended, and how many attempts), and **what it does now or what it waits for**. The missions, the dossier and what the controller decided stay one click away, inside the card.
2. **A new read model serves it**, `GET /api/v1/flow`, built from what the conductor keeps: the problems, their missions, the problems that wait in the line, the last thing each runner reported and when, and the state of the machines. It adds nothing that the stores do not hold; it is announced on the `missions` topic like the rest (ADR 0025), so a change reaches the screen at once. A clock on the screen moves the ages ("12 s ago") when nothing else moves.
3. **What a problem waits for is said, in the order a person can act on it**: another problem is being done on its repository (and which); the machines of its world are not free (and their states); every lane of its world is at work (ADR 0053, which replaced "the runner is busy with another problem": that was true only while a runner worked on one mission at a time); no runner has asked for work lately; or nothing is in the way and the runner takes it at its next call. A mission that the controller **sent back** waits in the same way until a runner is offered it again: it is not shown as running.
4. **A mission that reports nothing for ten minutes is told as quiet** (an agent thinks for minutes, not for this long); nothing is declared stuck by the screen.
5. **The order is stable**: what is going on keeps the order in which it was started, so that a card does not jump while it is read; what waits in the line comes next in the order it was validated; what is over comes last, the newest first, a few of them. The worlds shown are the ones the lead planned.
6. **The same row ticked again is the same problem** (its identifier is the row's): only the missions of the present run count in its attempts and in its detail.
7. **The Decisions screen says that a row is being worked on**, whatever the row is (a pull request or not): the row of a problem that is in progress or that waits in the line reads *in progress*, and what is ticked again is refused as already being worked on.
8. **Two small things for a phone**: while the lead plans, the button says so (*the lead is planning N*); the header keeps what a person acts on (the live state, the bell, who is signed in) and drops the name and the source badge on a narrow screen. The machines come after the problems on the Missions screen, since what a person follows is the problems.

## Consequences

- The priority is shown only when it is urgent (70 and above); a normal problem shows none. The waiting reasons are codes translated on the screen (`FlowWaiting*` keys), so a second language changes the words, not the rule.
- The read model is computed at each read from the stores (the window of the 200 most recent missions): enough for the test bed, and bounded; a deployment with many problems would page it.
- The screen is a **choice to confirm** (rule 16): the cards replace the flat list; the machines moved below the problems; the priority label; the text of every waiting reason.

## Not decided here, not built

- Showing the **history** of a problem across runs, and a view per repository or per world (the queue of a world as such).
- A **stuck** verdict: the screen tells that nothing was said for a while, and leaves the judgement to the person.
- A **notice in the bell** when a problem waits for the maintainer or leaves the line without starting (FR-VIEW-5 stays to be built).
