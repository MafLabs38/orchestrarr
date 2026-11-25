# 0053 — Each world has lanes, and a machine is never idle while a mission waits for it

- **Status**: proposed (accepted when the pull request that carries it is merged)
- **Date**: 2026-11-25
- **Spec**: D-107 ([SPEC.md](../SPEC.md)). Extends [ADR 0050](0050-the-flow-has-a-priority-from-0-to-100-and-a-failed-release-goes-ahead.md) (the flow and its priority), [ADR 0049](0049-problems-on-one-repository-are-done-one-at-a-time-and-a-conflict-is-not-mended-here.md) (the line of a repository) and [ADR 0047](0047-a-machine-has-a-state-and-leaves-a-reset-isolated-and-qualified.md) (the machines); changes the "one mission at a time" of the runner of [ADR 0042](0042-a-runner-serves-one-world-and-the-test-bed-validates-through-the-plan.md).

## Context

After the flow of ADR 0050 was demonstrated, the maintainer said (chat of 2026-11-25) that **the flow has a problem**, and asked whether it was one of architecture or of configuration. What he expects, in the words of a clean-room fab, where each world is a workshop and the lots are the code moving through them:

- **The Git world** has no machine: several repositories can be worked on in parallel. In practice the agents and the forge will saturate (a refusal, a ban), so there is a **limit**, but it is a setting, not a rule of the design.
- **The staging world** has the staging machines (the lab is meant to have two, of four cores each): each takes **one job**, and **two jobs run at once on two machines**. *Not using a machine that is free is a fault.*
- The release world is the same with its machines.
- The flow must **optimise the traffic**: nothing waits for something it does not need.

What the code did: the control plane already held the rules (a line per repository, one lease per machine, a mandate per runner, a priority that orders what is offered). **The runner did not**: its loop took one mission at a time **across all the worlds**, so a merge waited behind a five-minute proof in staging, and two staging machines could not have worked together, whatever the configuration said. And nothing stated the capacity of a world. So the answer to his question is: **both, and mostly the runner**: the mechanism was missing (architecture) and the capacity was not declared (configuration).

## Decision

1. **A lane is a place where one mission at a time is worked on.** The lanes of a world are: **one per staging machine**, **one per deployment machine**, and **`GitLanes` slots for the Git world** (no machine to tell them apart). A runner runs **one loop per lane**: each asks the control plane for a mission of its world (with its machine, for a world that has machines), works it, hands the dossier over and asks again. A lane that has nothing to do looks after its own machine (restores and checks it when it is Finished, gives back a lease that no mission holds); a machine is served by one lane only.
2. **The capacity of a world is configuration, and adding to it is a line of configuration.** `MissionRunner:StagingMachines` and `MissionRunner:DeployMachines` (lists of the aliases of the machines; `StagingMachine` and `DeployMachine` stay as the way to say there is one), and `MissionRunner:GitLanes` (1 to 10, **1 by default**). The control plane's `Missions:Machines` says the same machines to the pool. A machine is a lane of one world only (a deployment machine is never a staging machine, which is restored after each job). **The runner says its lanes to the control plane in its hello**, so that the screens can say why a mission waits.
3. **The rules of the flow do not change.** One problem in progress per repository (ADR 0049), so two lanes never work on one repository and two machines never collide on it; one lease per machine (ADR 0047); the priority orders what is offered (ADR 0050); nothing is pulled out of a lane in the middle of its mission. What changes is that **everything else now goes at the same time**: a merge and a deployment go on while staging proves another problem, and every machine has a job when there is a job for it.
4. **A mission is offered to one lane.** Lanes ask at the same time, so choosing the mission, leasing its machine and noting that it was offered are **one step** on the control plane, and a mission that was offered and has not started (a minute at most) is not offered to another lane. The lane of a machine that asks again for the mission it was offered gets it again (a runner that restarted before it could start it). Without this, two lanes would have been given one mission and two machines would have been restored for it.
5. **The screens say what a mission waits for, per world.** A world with machines waits for a machine when **none** of them is free (and names them all with their states); a world without one says **"every lane of this world is at work (Git 3/3)"** when the missions being worked on fill the lanes the runner announced. This replaces "the runner is busy with another problem" (ADR 0052), which was true only because of the single loop.
6. **One runner, many lanes.** The host's `Tasks:Runners` still accepts a single runner identity (ADR 0017: a batch of the typed-task path goes to whichever runner asks first, which is why a second identity is refused). The missions do not need a second identity: the mandate names the runner and the claim names the world and the machine. A runner of the platform side with several lanes is one identity and does not touch that rule.

## What this does not do

- **The lab has one staging machine and one release machine.** With one staging machine the staging world is still one job at a time, and it is the bottleneck of a busy day: its proof takes minutes and a restore follows each job. **A second staging machine is a second lane**: the VM, one line in the pool and one in the runner, no code. It is the maintainer's decision whether to make it (infrastructure on the hypervisor of the lab).
- **No pre-emption and no new queue per world.** A world's queue is the missions offered to its lanes in the order of ADR 0050; a queue of its own for the Git world is not needed while the act gateway merges at once.
- The offered-lease note is in memory like the other stores of the test bed; a restart forgets it, and a mission that was offered and not started is offered again.

## Defaults taken, for the maintainer to veto

- `GitLanes` is **1** in a product (what it did) and **3** in the test bed. The limit of 10 is a guard against a typo, not a recommendation: the forge and the agents are the real limit, and it is for the maintainer to set once he has seen how they react.
- A lane that has finished a staging mission restores and checks its machine before asking again, as before (ADR 0047): the restore time of a machine is counted in its lane, not in the others.

## Consequences

- A runner with several staging machines restores them in parallel through the lab tool, and runs the agents of several machines at once: the load on the runner's host and on the forge grows with the lanes, and the limit of the Git world is how to keep it in check.
- The flow screen shows the lanes at work (what each problem does and waits for, live), so that a person sees at once whether the flow is blocked or goes on.
- The scenario runner of the lab ticks a case with one problem at a time and is not affected; the demo (`lab/demo.sh`) is what shows the lanes together.
