using Orchestrarr.Core.Machines;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Host.Missions;

/// <summary>
/// One piece of equipment of a workshop (ADR 0056): a lab machine, or a slot of the forge, which has no machine. Its state is said in the words of a fab: <c>inProcess</c> (productive), <c>idle</c> (standby), <c>qualifying</c> (being restored
/// and checked, or waiting to be), <c>down</c> (unscheduled downtime, with its reason). What it works on is the operation of the mission that holds it.
/// </summary>
public sealed record BoardEquipment(string Id, string Kind, string State, string? ProblemId, string? Title, string? Repository, DateTimeOffset? Since, string? Reason, string? OperationKind = null);

/// <summary>A workshop (a world): its equipment, how many operations it works on now, how many wait in front of it, and why they wait (the codes of <see cref="FlowWaiting"/>, each once).</summary>
public sealed record BoardWorkshop(string World, IReadOnlyList<BoardEquipment> Equipment, int InProcess, int Waiting, IReadOnlyList<string> Why);

/// <summary>
/// One operation of a lot, placed where it is: <c>Place</c> is <c>queue</c> (validated, not started), a world, or <c>out</c> (over); <c>State</c> says what it does there, in a word a screen translates: <c>working</c>, <c>judging</c> (the
/// controller reads its dossier), <c>waitingMachine</c>, <c>waitingLane</c>, <c>waitingPickup</c>, <c>waitingRunner</c>, <c>waitingTurn</c>, <c>waitingBot</c>, <c>held</c> (a person held it, ADR 0056), <c>done</c> or <c>putAside</c>. <c>Detail</c> is the last thing said of it, or what it waits for.
/// </summary>
public sealed record BoardOperation(string ProblemId, string Title, string? Kind, string Repository, string Place, string State, string? Detail, int Priority, DateTimeOffset Since, string? Equipment);

/// <summary>A lot (a repository) and its operations: in process first, then the line in the order of its route, then what is over, the newest first.</summary>
public sealed record BoardLot(string Repository, IReadOnlyList<BoardOperation> Operations);

/// <summary>What a person counts at a glance: the operations in the line, in process, waiting in a workshop, done and put aside (among the recent ones), and since when the oldest of the line waits.</summary>
public sealed record BoardCounters(int Queued, int InProcess, int Waiting, int Done, int PutAside, DateTimeOffset? OldestQueuedSince, int Held = 0);

/// <summary>The factory view of the flow (ADR 0056): the places an operation can be in, in order, the workshops with their equipment, the lots with their operations, and the counters.</summary>
public sealed record FlowBoardView(IReadOnlyList<string> Places, IReadOnlyList<BoardWorkshop> Workshops, IReadOnlyList<BoardLot> Lots, BoardCounters Counters);

/// <summary>
/// The read model of the Flow screen (ADR 0056): the problems of <see cref="FlowView"/> arranged as a fab's factory view. It is computed from what the flow, the machines and the runners already say, and says nothing they do not.
/// </summary>
internal static class FlowBoard
{
    internal const string Queue = "queue";
    internal const string Out = "out";

    internal static FlowBoardView Build(IReadOnlyList<FlowProblem> all, IReadOnlyList<Machine> machines, IReadOnlyList<Mission> missions, Func<string, int> lanesOf)
    {
        // A row ticked again is the same problem: its new run (in the line or in process, which the flow lists first) is the one shown, not the run before it that is over
        var flow = all.DistinctBy(problem => problem.ProblemId, StringComparer.Ordinal).ToList();
        var missionById = missions.GroupBy(mission => mission.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var byProblem = flow.ToDictionary(problem => problem.ProblemId, StringComparer.Ordinal);
        var operations = flow.Select(problem => OperationOf(problem, machines)).ToList();
        var worlds = WorldsOf(flow, machines);
        var workshops = worlds.Select(world => WorkshopOf(world, operations, machines, missionById, byProblem, lanesOf(world))).ToList();
        var lots = operations.GroupBy(operation => operation.Repository, StringComparer.Ordinal)
            .OrderBy(lot => lot.Key, StringComparer.Ordinal)
            .Select(lot => new BoardLot(lot.Key, [.. lot.OrderBy(OrderInLot).ThenBy(operation => operation.Place == Out ? -operation.Since.ToUnixTimeMilliseconds() : operation.Since.ToUnixTimeMilliseconds())]))
            .ToList();
        var queued = operations.Where(operation => operation.Place == Queue).ToList();
        var counters = new BoardCounters(
            queued.Count,
            operations.Count(operation => operation.State is "working" or "judging"),
            operations.Count(operation => operation.Place is not (Queue or Out) && operation.State.StartsWith("waiting", StringComparison.Ordinal)),
            operations.Count(operation => operation.State == "done"),
            operations.Count(operation => operation.State == "putAside"),
            queued.Count > 0 ? queued.Min(operation => operation.Since) : null,
            queued.Count(operation => operation.State == "held"));
        return new FlowBoardView([Queue, .. worlds, Out], workshops, lots, counters);
    }

    // In a lot, what is being worked on comes first, then the line in the order of the route (ADR 0055), then what is over
    private static (int Group, int Rank) OrderInLot(BoardOperation operation) => operation.Place switch
    {
        Out => (2, 0),
        Queue => (1, RouteRank.Of(operation.Kind)),
        _ => (0, 0),
    };

    // The worlds that have a workshop now: the ones of a machine of the lab, the forge, and any world an operation is in or will go to
    private static List<string> WorldsOf(IReadOnlyList<FlowProblem> flow, IReadOnlyList<Machine> machines)
    {
        var present = new HashSet<World> { World.Git };
        foreach (var machine in machines)
        {
            _ = present.Add(machine.Role == MachineRole.Staging ? World.Staging : World.Release);
        }
        foreach (var step in flow.SelectMany(problem => problem.Steps))
        {
            if (Enum.TryParse<World>(step.World, out var world))
            {
                _ = present.Add(world);
            }
        }
        return [.. Enum.GetValues<World>().Where(present.Contains).Select(world => world.ToString())];
    }

    private static BoardOperation OperationOf(FlowProblem problem, IReadOnlyList<Machine> machines)
    {
        switch (problem.State)
        {
            case "queued":
                return new BoardOperation(problem.ProblemId, problem.Title, problem.Kind, problem.Repository, Queue, problem.Activity?.Waiting?.Code switch { "bot" => "waitingBot", "held" => "held", _ => "waitingTurn" },
                    problem.Activity?.Waiting?.Subject, problem.Priority, problem.StartedAt, null);
            case "inProgress":
                var step = CurrentStep(problem.Steps);
                var machine = step?.MissionId is { } missionId ? machines.FirstOrDefault(candidate => string.Equals(candidate.MissionId, missionId, StringComparison.Ordinal)) : null;
                return new BoardOperation(problem.ProblemId, problem.Title, problem.Kind, problem.Repository, step?.World ?? Queue, StateOf(problem.Activity), DetailOf(problem.Activity), problem.Priority,
                    problem.Activity?.At ?? problem.StartedAt, machine?.Id);
            default:
                return new BoardOperation(problem.ProblemId, problem.Title, problem.Kind, problem.Repository, Out, problem.State == "closed" ? "done" : "putAside", problem.Note, problem.Priority, problem.StartedAt, null);
        }
    }

    // The world an operation in progress is in: the one whose mission runs, is read or waits; else the first that has not started (its mandate is about to be issued)
    private static FlowStep? CurrentStep(IReadOnlyList<FlowStep> steps) =>
        steps.FirstOrDefault(step => step.State is "running" or "reading" or "waiting") ?? steps.FirstOrDefault(step => step.State == "notStarted") ?? steps.LastOrDefault();

    private static string StateOf(FlowActivity? activity) => activity switch
    {
        { Kind: "running" } => "working",
        { Kind: "controller" } => "judging",
        { Waiting.Code: "machine" } => "waitingMachine",
        { Waiting.Code: "busy" } => "waitingLane",
        { Waiting.Code: "turn" } => "waitingTurn",
        { Waiting.Code: "bot" } => "waitingBot",
        { Waiting.Code: "runner" } => "waitingRunner",
        _ => "waitingPickup",
    };

    private static string? DetailOf(FlowActivity? activity) => activity?.Kind == "running" ? activity.Text : activity?.Waiting?.Subject;

    private static BoardWorkshop WorkshopOf(string world, IReadOnlyList<BoardOperation> operations, IReadOnlyList<Machine> machines, Dictionary<string, Mission> missionById, Dictionary<string, FlowProblem> byProblem, int lanes)
    {
        var here = operations.Where(operation => operation.Place == world).ToList();
        var working = here.Where(operation => operation.State is "working" or "judging").ToList();
        var waiting = here.Where(operation => operation.State.StartsWith("waiting", StringComparison.Ordinal)).ToList();
        var role = world switch { nameof(World.Staging) => MachineRole.Staging, nameof(World.Release) => MachineRole.Release, _ => (MachineRole?)null };
        List<BoardEquipment> equipment;
        if (role is { } wanted)
        {
            equipment = [.. machines.Where(machine => machine.Role == wanted).OrderBy(machine => machine.Id, StringComparer.Ordinal).Select(machine => EquipmentOf(machine, missionById, byProblem))];
        }
        else
        {
            // The forge has no machine: its equipment is its slots (the lanes the runners said they work on at once), filled by the operations it works on; with no lane said, one slot per operation at work
            var slots = Math.Max(lanes, working.Count);
            equipment = [.. Enumerable.Range(0, slots).Select(index => index < working.Count
                ? new BoardEquipment($"slot-{index + 1}", "slot", "inProcess", working[index].ProblemId, working[index].Title, working[index].Repository, working[index].Since, null, working[index].Kind)
                : new BoardEquipment($"slot-{index + 1}", "slot", "idle", null, null, null, null, null))];
        }
        return new BoardWorkshop(world, equipment, working.Count, waiting.Count, [.. waiting.Select(operation => operation.State).Distinct(StringComparer.Ordinal)]);
    }

    private static BoardEquipment EquipmentOf(Machine machine, Dictionary<string, Mission> missionById, Dictionary<string, FlowProblem> byProblem)
    {
        var state = machine.State switch
        {
            MachineState.InProcess => "inProcess",
            MachineState.Idle => "idle",
            MachineState.Down => "down",
            // A machine whose job is over is not qualified any more: it is restored and checked before the next one
            _ => "qualifying",
        };
        var problem = machine.MissionId is { } id && missionById.TryGetValue(id, out var mission) && byProblem.TryGetValue(mission.ProblemId, out var found) ? found : null;
        return new BoardEquipment(machine.Id, "machine", state, problem?.ProblemId, problem?.Title, problem?.Repository, machine.Since, machine.State == MachineState.Down ? machine.Reason : null, problem?.Kind);
    }
}
