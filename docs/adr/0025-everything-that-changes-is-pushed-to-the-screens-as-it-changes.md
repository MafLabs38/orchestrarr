# 0025 — Everything that changes is pushed to the screens as it changes

- **Status**: proposed (asked for by the maintainer on 2026-11-11; accepted when the pull request that carries it is merged)
- **Date**: 2026-11-11
- **Spec**: D-73

## Context

The screens read the API every thirty seconds, and the executor sent the trace of a run only when the run ended. A person who started a run saw nothing happen for minutes, then everything at once: it looks like a black box, and it hides what a product that acts on infrastructure must show, which is what is happening now.

## Decision

1. **What changes is pushed.** The host opens a WebSocket (SignalR, which falls back to server-sent events or long polling where a proxy refuses it) at `/api/v1/live`, for whoever may read the fleet. It announces **that something changed, never the data**: a message is `(topic, id)`, with the topics `runs` (and the id of the run), `approvals`, `audit`, `status`, `problems`, `accounts` and `options`. The screen then reads what changed through the API, with its session, like any other read; the WebSocket therefore adds no way to read anything.
2. **Announced once the change is made.** A write of the API that succeeds, a batch taken by an executor, a line of a trace, the end of a run, an executor that appears or comes back, and a read of the fleet or of the pull requests that brings something new, each announce the topics they touch. A burst is coalesced: each distinct `(topic, id)` is sent once per quarter of a second, and the last one is never dropped.
3. **The executor sends the trace while the run goes on**, every second, in order, once each; a request that fails ends the live sending of that run and the rest is sent when the run ends.
4. **Polling stays as the fallback**, every thirty seconds, so that a page whose WebSocket cannot open (not signed in, a proxy) still moves. A badge in the header says whether the page is live. After a reconnection the page reads everything again, since what changed while it was away was not announced.
5. **A rule for every screen to come**: a screen that shows something that changes by itself (a state, a log, a list another person can change) is wired to a topic. Polling alone is a defect, not a design.

## Consequences

- A reverse proxy in front of the control plane must pass WebSocket upgrades (or accept the fallback).
- A client that stays connected after signing out receives pings it can do nothing with; the data is behind the API.
- The messages carry identifiers of runs, not secrets. A client that is not allowed to read the fleet cannot connect.
