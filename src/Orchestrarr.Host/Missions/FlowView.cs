using Orchestrarr.Core.Machines;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Host.Missions;

/// <summary>Where one world of a problem stands: <c>notStarted</c>, <c>waiting</c> (a mandate that no runner has taken), <c>running</c>, <c>reading</c> (the controller reads the dossier), <c>done</c> or <c>ended</c> (not done). <c>Attempts</c> counts the missions of that world, a world that is done again after a release that failed.</summary>
public sealed record FlowStep(string World, string State, string? MissionId, int Attempts);

/// <summary>Why a problem does not move: <c>turn</c> (another problem is being done on its repository), <c>machine</c> (the machine of its world is not free), <c>busy</c> (the runner works on another problem), <c>runner</c> (no runner has asked for work lately) or <c>pickup</c> (nothing is in the way, the runner takes it at its next call).</summary>
public sealed record FlowWaiting(string Code, string? Subject);

/// <summary>What a problem does now: the last thing its runner reported (<c>running</c>), the controller reading the dossier (<c>controller</c>), or what it waits for (<c>waiting</c>). <c>At</c> is when it last moved.</summary>
public sealed record FlowActivity(string Kind, string? Text, DateTimeOffset? At, FlowWaiting? Waiting);

/// <summary>One problem as a person follows it: what it is, where it stands in each world, what it does now, how urgent it is.</summary>
public sealed record FlowProblem(string ProblemId, string Title, string? Kind, string Repository, string State, int Priority, DateTimeOffset StartedAt, IReadOnlyList<FlowStep> Steps, FlowActivity? Activity, string? Note);

/// <summary>
/// The read model of the Missions screen (the maintainer, 2026-11-25: follow the flow, understand what happens and where, whether it is blocked or active). It is computed from what the conductor keeps and says nothing the stores do not: the problems that wait in the
/// line (ADR 0049), the missions of each world, the last thing each runner reported, and the state of the machines.
/// </summary>
internal static class FlowView
{
    private const int MaxFinished = 20;

    internal static IReadOnlyList<FlowProblem> Build(IReadOnlyList<Mission> recent, IProblemStore problems, IReadOnlyList<QueuedProblem> waiting, IReadOnlyList<Machine> machines, bool runnerConnected, Func<string, (string? Line, DateTimeOffset? At)> lastNews, Func<Mission, DateTimeOffset?> issuedAt, Func<Mission, bool>? reworkPending = null, Func<string, int>? lanesOf = null)
    {
        reworkPending ??= _ => false;
        lanesOf ??= _ => 0;
        // The newest missions first: the order of the problems follows the last thing that happened to each
        var byProblem = recent.GroupBy(mission => mission.ProblemId, StringComparer.Ordinal).ToList();
        var runs = problems.GetMany([.. byProblem.Select(group => group.Key)]);
        var inProgress = problems.InProgress();
        var working = new List<(ProblemRun Run, FlowProblem Flow)>();
        var finished = new List<FlowProblem>();
        foreach (var group in byProblem)
        {
            if (!runs.TryGetValue(group.Key, out var run))
            {
                continue;
            }
            // The same row ticked again later is the same problem (its identifier is the row's): only the missions of this run are the problem's now
            var flow = Of(run, [.. group.Where(mission => issuedAt(mission) is not { } at || at >= run.StartedAt)], inProgress, machines, runnerConnected, lastNews, reworkPending, recent, lanesOf);
            if (run.State == ProblemState.InProgress)
            {
                working.Add((run, flow));
            }
            else
            {
                finished.Add(flow);
            }
        }
        // What is going on keeps its place from one read to the next (the order it was started in), so that a card does not jump while it is read; what is over comes last, the newest first
        var going = working.OrderBy(pair => pair.Run.StartedAt).ThenBy(pair => pair.Run.ProblemId, StringComparer.Ordinal).Select(pair => pair.Flow).ToList();
        var started = going.Select(flow => flow.ProblemId).ToHashSet(StringComparer.Ordinal);
        // A problem that has just left the line and is not yet saved as started is in both lists for an instant: it is shown once
        var queued = waiting.Where(problem => !started.Contains(problem.Entry.ProblemId)).OrderBy(problem => problem.QueuedAt).Select(problem => Queued(problem, inProgress)).ToList();
        return [.. going, .. queued, .. finished.Take(MaxFinished)];
    }

    private static FlowProblem Of(ProblemRun run, IReadOnlyList<Mission> missions, IReadOnlyList<ProblemRun> inProgress, IReadOnlyList<Machine> machines, bool runnerConnected, Func<string, (string? Line, DateTimeOffset? At)> lastNews,
        Func<Mission, bool> reworkPending, IReadOnlyList<Mission> all, Func<string, int> lanesOf)
    {
        var active = missions.FirstOrDefault(mission => !mission.IsOver);
        return new FlowProblem(run.ProblemId, TitleOf(run.Entry), run.Entry.Brief?.Kind ?? run.Entry.ProblemClass, run.Entry.Repositories.FirstOrDefault() ?? string.Empty, StateOf(run.State), run.Priority, run.StartedAt,
            StepsOf(run, missions, reworkPending), run.State == ProblemState.InProgress && active is not null ? ActivityOf(active, run, inProgress, machines, runnerConnected, lastNews, reworkPending(active), LoadOf(active.World, all, reworkPending, lanesOf)) : null, run.Note);
    }

    private static FlowProblem Queued(QueuedProblem problem, IReadOnlyList<ProblemRun> inProgress)
    {
        var entry = problem.Entry;
        var ahead = AheadOf(entry.Repositories.FirstOrDefault(), entry.ProblemId, inProgress);
        return new FlowProblem(entry.ProblemId, TitleOf(entry), entry.Brief?.Kind ?? entry.ProblemClass, entry.Repositories.FirstOrDefault() ?? string.Empty, "queued", FlowPriority.Normal, problem.QueuedAt,
            [.. Worlds(entry.MaxWorld, true).Select(world => new FlowStep(world.ToString(), "notStarted", null, 0))], new FlowActivity("waiting", null, problem.QueuedAt, new FlowWaiting("turn", ahead is null ? null : TitleOf(ahead.Entry))), null);
    }

    private static string StateOf(ProblemState state) => state switch
    {
        ProblemState.InProgress => "inProgress",
        ProblemState.Escalated => "escalated",
        ProblemState.Closed => "closed",
        ProblemState.Failed => "failed",
        ProblemState.SetAside => "setAside",
        _ => "inPlan",
    };

    // What a person reads as the name of the problem: what the control plane read of the row (an image, a version, a count of alerts), then where
    private static string TitleOf(PlanEntry entry) => entry.Brief is { Description.Length: > 0 } brief ? brief.Description : entry.Objective;

    private static IEnumerable<World> Worlds(World highest, bool uses) =>
        Enum.GetValues<World>().Where(world => world <= highest && (uses || world != World.Staging));

    private static List<FlowStep> StepsOf(ProblemRun run, IReadOnlyList<Mission> missions, Func<Mission, bool> reworkPending)
    {
        var steps = new List<FlowStep>();
        // The worlds the lead planned (a compose stack is planned up to the Git world): a world that the problem will never reach is not shown as waiting for it
        foreach (var world in Worlds(run.Entry.Plan?.Last ?? run.Entry.MaxWorld, run.UsesStaging))
        {
            var ofWorld = missions.Where(mission => mission.World == world).ToList();
            steps.Add(ofWorld.Count == 0 ? new FlowStep(world.ToString(), "notStarted", null, 0) : new FlowStep(world.ToString(), StepState(ofWorld[0], reworkPending(ofWorld[0])), ofWorld[0].Id, ofWorld.Count));
        }
        return steps;
    }

    // A mission that the controller sent back is "running" in the store as soon as it is sent back, but nobody works on it until a runner is offered it again: it waits
    private static string StepState(Mission mission, bool reworkPending) => mission.State switch
    {
        MissionState.Running => reworkPending ? "waiting" : "running",
        MissionState.HandingOver => "reading",
        MissionState.Accepted => "done",
        MissionState.Ended => mission.End == MissionEnd.Done ? "done" : "ended",
        _ => "waiting",
    };

    private static FlowActivity ActivityOf(Mission mission, ProblemRun run, IReadOnlyList<ProblemRun> inProgress, IReadOnlyList<Machine> machines, bool runnerConnected, Func<string, (string? Line, DateTimeOffset? At)> lastNews, bool reworkPending,
        (int Running, int Lanes) load)
    {
        var (line, at) = lastNews(mission.Id);
        if (reworkPending)
        {
            return new FlowActivity("waiting", null, at, WaitingOf(mission, run, inProgress, machines, runnerConnected, load));
        }
        return mission.State switch
        {
            MissionState.Running => new FlowActivity("running", line, at, null),
            MissionState.HandingOver => new FlowActivity("controller", null, at, null),
            _ => new FlowActivity("waiting", null, at, WaitingOf(mission, run, inProgress, machines, runnerConnected, load)),
        };
    }

    // How many missions of the world are worked on now, and how many the runner works on at once there (0: it did not say). A sent-back mission that nobody has taken again is not worked on
    private static (int Running, int Lanes) LoadOf(World world, IReadOnlyList<Mission> all, Func<Mission, bool> reworkPending, Func<string, int> lanesOf) =>
        (all.Count(mission => mission.World == world && mission.State == MissionState.Running && !reworkPending(mission)), lanesOf(world.ToString()));

    // The first thing in the way, in the order a person can do something about it: another problem on the repository, the machine, the runner
    private static FlowWaiting WaitingOf(Mission mission, ProblemRun run, IReadOnlyList<ProblemRun> inProgress, IReadOnlyList<Machine> machines, bool runnerConnected, (int Running, int Lanes) load)
    {
        var ahead = AheadOf(run.Entry.Repositories.FirstOrDefault(), run.ProblemId, inProgress);
        if (ahead is not null)
        {
            return new FlowWaiting("turn", TitleOf(ahead.Entry));
        }
        var role = mission.World switch { World.Staging => MachineRole.Staging, World.Release => MachineRole.Release, _ => (MachineRole?)null };
        // A world with machines waits for one that is free: it says which are not, and why (several machines: none of them is free)
        if (role is { } wanted && machines.Where(machine => machine.Role == wanted).ToList() is { Count: > 0 } ofRole && ofRole.TrueForAll(machine => machine.State != MachineState.Idle))
        {
            return new FlowWaiting("machine", string.Join(", ", ofRole.Select(machine => $"{machine.Id} ({machine.State})")));
        }
        // Every lane of the world is at work: while they are, nothing is asked for, which is not the same as being away
        if (load.Lanes > 0 && load.Running >= load.Lanes)
        {
            return new FlowWaiting("busy", $"{mission.World} {load.Running}/{load.Lanes}");
        }
        return runnerConnected ? new FlowWaiting("pickup", null) : new FlowWaiting("runner", null);
    }

    // The problem that holds the repository before this one: the oldest in progress on it (the rule of the turn, FR-MIS-6)
    private static ProblemRun? AheadOf(string? repository, string problemId, IReadOnlyList<ProblemRun> inProgress) =>
        repository is null ? null : inProgress.Where(other => other.ProblemId != problemId && other.Entry.Repositories.Count == 1 && string.Equals(other.Entry.Repositories[0], repository, StringComparison.Ordinal))
            .OrderBy(other => other.StartedAt).ThenBy(other => other.ProblemId, StringComparer.Ordinal).FirstOrDefault();
}
