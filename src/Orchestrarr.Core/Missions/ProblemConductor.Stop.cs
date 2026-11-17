namespace Orchestrarr.Core.Missions;

/// <summary>The stop, the budget and the sweep of the conductor: what ends a problem that no runner will end for itself.</summary>
public sealed partial class ProblemConductor
{
    /// <summary>The global stop, the maintainer or the budget: every mandate of the problem is revoked and the running mission ends cleanly.</summary>
    public async Task<ConductResult> StopAsync(string problemId, string reason, CancellationToken cancellationToken) => await LockedAsync(problemId, () => StopLockedAsync(problemId, reason), cancellationToken);

    /// <summary>The mission used up its budget: it ends as failed and the problem fails with it; a loop never goes on past a budget.</summary>
    public async Task<ConductResult> BudgetExhaustedAsync(string missionId, CancellationToken cancellationToken) =>
        await OnMissionAsync(missionId, async mission => problems.Get(mission.ProblemId) is { } run ? await EndAsync(run, mission, MissionEvent.BudgetExhausted, "the budget is used up") : ConductResult.Fail("the problem of this mission is unknown"), cancellationToken);

    /// <summary>
    /// What no runner will ever say for itself: with the global stop engaged every problem in progress is stopped; a mandate that expired while its mission had not handed over ends it (a runner
    /// that disappeared would otherwise leave the problem in progress for ever); a problem that has run for longer than its bound is stopped. Returns how many problems were ended.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var ended = 0;
        foreach (var problemId in problems.InProgress().Select(run => run.ProblemId))
        {
            // One run that cannot be read or stopped never keeps the others from being stopped: the failure is written down and the pass goes on
            try
            {
                // The reason is found **under the lock of the problem**, on the problem as it is then: an operation that is moving the problem between two worlds has the lock, so a problem is never stopped on
                // a state that is only half way through a move, and the reason written in the trail is the true one
                var stopped = await LockedAsync(problemId, async () => problems.Get(problemId) is { State: ProblemState.InProgress } current && SweepReason(current) is { } reason
                    ? await StopLockedAsync(problemId, reason)
                    : ConductResult.Fail("nothing to stop"), cancellationToken);
                if (stopped.Ok)
                {
                    ended++;
                }
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                await BestEffortAsync(MissionAuditEvents.SweepFailed, problemId, Text($"{failure.GetType().Name}: the next pass tries again"));
            }
        }
        return ended;
    }

    private string? SweepReason(ProblemRun run)
    {
        var now = time.GetUtcNow();
        if (stop.Engaged)
        {
            return "the global stop is engaged";
        }
        if (now - run.StartedAt >= options.Loop.MaxTimePerProblem)
        {
            return "the problem reached its time limit";
        }
        if (HighestNow(run.Entry, run.Approval) is not { } highest)
        {
            return "the grant of the application was withdrawn";
        }
        if (run.CurrentWorld is { } current && current > highest)
        {
            return "the grant of the application was lowered below the world that is running";
        }
        var expired = missions.ForProblem(run.ProblemId).Any(mission => mission.State is MissionState.Mandated or MissionState.Running
            && mission.MandateId is { } id && mandates.Get(id) is { } signed && now >= signed.Mandate.ExpiresAt);
        if (expired)
        {
            return "the mandate expired";
        }
        // A crash between two writes (the mission ended, the problem not yet moved) leaves a problem in progress with nothing running: no other mission follows, so the work stopped half done
        var all = missions.ForProblem(run.ProblemId);
        return all.Count > 0 && all.All(mission => mission.IsOver) ? "the work stopped half done: no mission is running and none follows" : null;
    }

    private async Task<ConductResult> StopLockedAsync(string problemId, string reason)
    {
        if (problems.Get(problemId) is not { State: ProblemState.InProgress } run)
        {
            return ConductResult.Fail("the problem is not being worked on");
        }
        Mission? last = null;
        foreach (var mission in missions.ForProblem(problemId))
        {
            await RevokeAsync(mission);
            if (!mission.IsOver && MissionMachine.Apply(mission, MissionEvent.Stopped, options.Loop, reason: reason) is { Mission: { } ended })
            {
                missions.Save(ended);
                await BestEffortAsync(MissionAuditEvents.MissionEnded, mission.Id, Text($"stopped: {reason}"));
                last = ended;
            }
        }
        problems.Save(run with { State = ProblemState.Failed, Note = reason });
        await BestEffortAsync(MissionAuditEvents.ProblemFailed, problemId, reason);
        // The problem moved to Failed even when no mission was running: that is a stop that worked
        return new ConductResult(true, null, [], last, null);
    }
}
