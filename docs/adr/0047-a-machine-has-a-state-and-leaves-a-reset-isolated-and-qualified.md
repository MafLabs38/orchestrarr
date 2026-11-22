# 0047 — A machine has a state, and leaves a reset isolated and qualified

- **Status**: proposed (accepted when the pull request that carries it is merged)
- **Date**: 2026-11-22
- **Spec**: D-101 ([SPEC.md](../SPEC.md)). Makes FR-STG-8 and FR-STG-9 mandatory ([FUNCTIONAL.md](../FUNCTIONAL.md)); builds on [ADR 0034](0034-staging-is-a-prerequisite-per-application.md), [ADR 0035](0035-the-frame-is-enforced-by-the-environment.md) and [ADR 0041](0041-a-problem-is-on-one-repository-and-the-staging-is-held-only-while-it-is-needed.md).

## Context

The worker agent of the staging world must be free inside its machine: it installs, builds, tests and breaks what it needs, with no rule about what it does there. That freedom is only acceptable if **the machine is the boundary**: nothing it does can leave it. And a machine that runs one job must not be given another while the first runs, nor after it, before it is restored.

**The maintainer decided this in the chat of 2026-11-22**, in the words of a clean-room fab: the two lab machines (the staging forge and the release forge) are production equipment. A machine has a **status** (idle, in process, finished, and whether it is *viable* or awaiting repair); a lot started on it takes it; no other lot starts on it until it is qualified again. Two things are **mandatory** for Orchestrarr to work: **a machine that comes out of a reset is isolated by design**, and **its status is controlled**.

The specification had half of it: a slot status and a restore before reuse (FR-STG-8, decided), and an isolation of the slot as a proposal (FR-STG-9). The code had neither: only one runner existed, so two jobs on one machine could not happen by luck.

## Decision

1. **Every lab machine has a state kept by the control plane**: **Down** (not viable: its qualification failed or it cannot be reached; the reason is kept, it awaits repair), **Qualifying** (being reset and checked), **Idle** (qualified and free), **InProcess** (leased to one mission) and **Finished** (the job is over; the machine is not qualified and cannot take another job). The transitions are the only ones that exist: Idle → InProcess (a lease), InProcess → Finished (the mission ends in any way, a stop included), Finished → Qualifying (a reset), Qualifying → Idle (every check passed) or → Down (one failed), Down → Qualifying (a repair, asked by a person or by the platform step). A machine is Idle only after a qualification by this control plane: on start, every machine is Qualifying.
2. **A job takes a machine by a lease, and a machine has one holder.** A mission of the staging world is claimed only with a lease on an Idle machine; none free, the mission waits. The lease belongs to the mission and is returned when the mission ends; a lease that is not renewed by a living runner expires into Finished (the machine is never left InProcess by a runner that died). There is **no time limit of the lease** other than the budget of the mission, which is generous and configurable: a long troubleshooting is normal.
3. **Qualification is a set of checks, and isolation is one of them.** A machine becomes Idle only if: the baseline was restored, it answers the platform channel, **a probe made from inside the machine reaches none of the addresses it must not reach** (the control plane, the other lab machine, the hypervisor, the other hosts of the network: the canaries are configuration), and it reaches what the work needs (the package registries and the model provider). The result and the time are kept. A probe that finds a way out makes the machine **Down**: it is not given to a job.
4. **Isolation is set by the environment and is part of the baseline.** The hypervisor firewall of the machine denies its way to the local network (IPv4 and IPv6) and allows the Internet, name resolution and the platform channel in; the setting is in the reference snapshot, so that **a machine that comes out of a reset is isolated by construction**, and the qualification proves it each time. Inside the machine the agent has every freedom, root included; what it does there is judged on what leaves (the carried change, the dossier), by the rest of the architecture.
5. **The release machine has the same states without the reset.** What it runs must stay after the job: its qualification is isolation, reachability and health, never a restore, and it is Idle between deployments.
6. **The status is visible**: the control plane exposes the machines and their state, and the Missions screen shows them (live), with the reason when one is Down.

## Built

- The pool (`MachinePool`), the transitions that exist (`MachineRules`), the lease with one holder, the expiry of a lease whose runner gave no sign of life (generous: `Missions:MachineLeaseMinutes`), and the statistics (`MachineMetrics`: time in each state, availability, utilisation, lots, down events, mean time to repair), kept in the SQLite file with every transition.
- A claim of a staging or release mission carries the machine of the runner and is offered only with a lease on an Idle one. The runner gives the machine back when the mission is over, then **restores it (staging) and probes it from inside** (`MachineQualifier`); when it is idle it restores a machine that is Finished or Qualifying (the control plane started, a person asked) and gives back a lease that nobody works on.
- The hypervisor firewall of both lab machines denies their way to the local network and is **re-applied by the lab tool before a machine starts after any restore**, because a restore brings back the network settings of the snapshot. A machine whose rules are missing is not started: the restore fails and the machine is Down with the reason.
- The Missions screen shows the machines live (the `machines` topic of the live channel), with the checks of the last qualification, the reason when one is down, the statistics, and a button to qualify again.

## Consequences

- The hypervisor access of the platform step is limited to the pool of lab machines (FR-STG-9); the control plane stores the state, the runner of the platform side executes the reset and the probe and reports.
- A new mission on a machine that is not Idle waits, which is the clean-room rule and also what makes two problems at once safe.
- The qualification can fail for a reason that is nobody's fault (the firewall flag lost by a restore of an old snapshot): the machine goes Down and says why, instead of working open.
- Not built here: more than two machines, and a machine that is repaired by itself (re-applying the setting). A person or the platform step asks the requalification.
