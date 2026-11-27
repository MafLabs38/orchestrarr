namespace Orchestrarr.Core.Missions;

/// <summary>
/// A merge that turned the integration branch red (ADR 0055): the change is in, so going back cannot un-merge it, and ending the problem would leave a red branch that holds every other operation of the lot. The problem is fixed
/// forward, as a developer does: it goes back to the staging world as a problem with no pull request, where a free agent (ADR 0048) works on the integration branch as it is now and brings its pipeline back to green; the gateway
/// carries that change into a pull request, and the Git world merges it with every pipeline green. Same approval, same budgets and bounds of the loop, no other validation; the problem goes ahead of the normal ones (a hot lot,
/// ADR 0050), and its lot waits because the problem is still in progress on it.
/// </summary>
public sealed partial class ProblemConductor
{
    private async Task<ConductResult> FixForwardAsync(Mission mission, ProblemRun run, IReadOnlyList<PreconditionFailure> failures)
    {
        // A fix needs a world where an agent can work: an application with no staging has none, and the work stops with the reason
        if (!run.UsesStaging)
        {
            return Moved(await EndAsync(run, mission, MissionEvent.EndedByController, "the merge turned the integration branch red and the application has no staging world to fix it in"), "the integration branch is red: the work stops", failures);
        }
        // The power is read again here, as at every crossing
        if (HighestNow(run.Entry, run.Approval) is not { } highest || highest < World.Git)
        {
            return await EndAsync(run, mission, MissionEvent.EndedByController, "the powers of the problem no longer reach the Git world, or the grant was withdrawn");
        }
        var merged = dossiers.Get(mission.Id)?.Commit;
        var transition = MissionMachine.Apply(mission, MissionEvent.EndedByController, options.Loop, reason: "the merge turned the integration branch red: the problem goes back to staging to fix it forward");
        if (transition.Mission is not { } ended)
        {
            return ConductResult.Fail(transition.Refusal ?? "refused");
        }
        string? revoked = null;
        unit.Atomically(() =>
        {
            missions.Save(ended);
            revoked = RevokeStore(mission);
        });
        await TellRevokedAsync(mission, revoked);
        await BestEffortAsync(MissionAuditEvents.MissionEnded, mission.Id, Text($"the integration branch is red after the merge{(merged is null ? "" : $" ({merged})")}: back to staging to fix it forward"));
        // What the host held of the earlier change is about a change that is merged now
        restarts?.Restarted(run.ProblemId);
        var entry = FixForwardEntry(run.Entry, merged, highest);
        var priority = FlowPriority.Raised(run.Priority);
        var opened = await OpenAsync(run with { Entry = entry, CurrentWorld = World.Staging, Priority = priority }, World.Staging, MandateSource.Controller);
        await BestEffortAsync(MissionAuditEvents.PriorityRaised, run.ProblemId, Text($"the integration branch is red after the merge: priority {run.Priority} to {priority}"));
        return opened.Ok ? Moved(opened, "the merge turned the integration branch red: the problem goes back to staging to fix it forward", failures) : new ConductResult(false, opened.Refusal, opened.Failures, ended, null);
    }

    // The problem as it is worked now: no pull request (the change is merged), the objective and the instruction of the staging world say what to fix, and the later worlds of the plan stay what the lead planned
    internal static PlanEntry FixForwardEntry(PlanEntry entry, string? merged, World highest)
    {
        var change = entry.PullRequest is { } number && entry.Repositories.Count == 1 ? Text($"pull request #{number} of {entry.Repositories[0]}") : Text($"the change for \"{entry.Objective}\"");
        var at = merged is { Length: >= 8 } commit ? Text($" at commit {commit[..8]}") : "";
        var objective = Text($"The integration branch of {string.Join(", ", entry.Repositories)} is green again: the merge of {change}{at} turned its pipeline red.");
        var instruction = Text($"The merge of {change} into the integration branch{at} turned the pipeline of the integration branch red. Find out why (run the steps of its pipeline here, on the branch as it is now) and make the change that brings it back to green. {PipelineChanges.RepairRules} Keep what was merged unless it cannot be made to work; if it must be undone, say so and why.");
        var later = entry.Plan is { Steps.Count: > 0 } plan
            ? plan.Steps.Where(step => step.World > World.Staging).ToList()
            : [.. Enum.GetValues<World>().Where(world => world > World.Staging && world <= highest).Select(world => new PlannedStep(world, null))];
        if (later.Count == 0)
        {
            later.Add(new PlannedStep(World.Git, null));
        }
        var steps = new List<PlannedStep> { new(World.Staging, instruction) };
        steps.AddRange(later);
        var reasons = entry.Plan?.Reasons ?? [];
        return entry with
        {
            Objective = objective,
            PullRequest = null,
            HeadSha = null,
            Plan = new LeadPlan(steps, Complex: false, ComplexReason: null, [.. reasons, "the merge turned the integration branch red: fixed forward in staging"]),
        };
    }
}
