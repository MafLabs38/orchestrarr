namespace Orchestrarr.Core.Missions;

/// <summary>
/// What happens to a mission that failed a check of the code, or that the controller judged to have failed: **one table, one place** (the order of the cases is the precedence).
/// A release that does not verify is a failed push (D-93, ADR 0040): its mission ends, the problem stays in progress and the loop starts again in the first world, through the same worlds and the same
/// checks, under the same approval, with the same budgets and without another validation.
/// </summary>
public sealed partial class ProblemConductor
{
    private const string ExecutedFilesReason = "the change touches files that the pipeline executes, and the merge may deploy: a person decides";

    private enum Route
    {
        /// <summary>The mission goes back to its worker where it is (the number of send-backs is bounded).</summary>
        SendBackInPlace,

        /// <summary>A person decides.</summary>
        Escalate,

        /// <summary>The work stops: nothing that going back could change.</summary>
        Stop,

        /// <summary>The release did not verify: the problem starts again in the first world.</summary>
        BackToFirstWorld,

        /// <summary>The merge turned the integration branch red: the problem goes back to the staging world to bring it back to green (ADR 0055).</summary>
        FixForward,
    }

    // The precedence: what a person must decide first, then what stops the work, then a pipeline result that is not known yet (a verdict cannot be taken on a fact that is not there: the mission goes back
    // where it is), then, in the release world, what a restart repairs: a pipeline that failed, or a release that the controller judged failed on facts that are known. An artifact that is not the one proved in
    // staging is an anomaly of the pipeline or of the supply chain, that code does not repair (D-90).
    private static (Route Route, string Why) RouteOf(World world, IReadOnlyList<PreconditionFailure> failures, bool controllerJudgedFailed = false, bool underStandingRule = false)
    {
        if (failures.Contains(PreconditionFailure.ExecutedFilesTouchedWhereMergeDeploys))
        {
            return (Route.Escalate, ExecutedFilesReason);
        }
        // An artifact that is not the one proved is a person's before anything else, a used-up budget included: an anomaly of the pipeline must reach a person and leave its trace
        if (world == World.Release && failures.Contains(PreconditionFailure.ArtifactNotProvedInStaging))
        {
            return (Route.Escalate, "the artifact that runs is not the one proved in staging: a person decides");
        }
        if (failures.Contains(PreconditionFailure.KillSwitchEngaged))
        {
            return (Route.Stop, "the global stop is engaged");
        }
        var repairsByRestart = world == World.Release && !failures.Contains(PreconditionFailure.CiResultNotKnown)
            && (controllerJudgedFailed || (failures.Count > 0 && failures.All(failure => failure is PreconditionFailure.CiNotGreenOnExactCommit)));
        // A release that failed under a standing rule is a person's: nobody approved that problem. It comes before the stops of the loop and of the budget, so that the maintainer is told what failed (the global stop
        // above is the only thing that comes first: a stop is a stop)
        if (underStandingRule && repairsByRestart)
        {
            return (Route.Escalate, "a release that was approved by a standing rule did not verify: nobody approved this problem, a person decides");
        }
        // In the release world a commit that is not the one the controller accepted at the crossing is an anomaly of the delivery, not a defect that a restart repairs: the conductor compares the two commits it
        // holds, whatever the controller decided, so a verdict cannot steer it
        if (world == World.Release && failures.Contains(PreconditionFailure.ReleaseCommitIsNotTheAcceptedOne))
        {
            return (Route.Escalate, "the commit that is released is not the one that was accepted: a person decides");
        }
        if (failures.Contains(PreconditionFailure.LoopLimitReached))
        {
            return (Route.Stop, "the loop reached its limit");
        }
        // A loop never goes on past a budget (D-89): wherever the budget is said to be used up, the work stops
        if (failures.Contains(PreconditionFailure.BudgetUnavailable))
        {
            return (Route.Stop, "the budget is used up");
        }
        // A merge on a branch that was red before it: the change is in, and no mission of this problem can make the branch green (ADR 0055)
        if (world == World.Git && failures.Contains(PreconditionFailure.IntegrationBranchRedBeforeMerge))
        {
            return (Route.Escalate, "the change is merged, and the integration branch was already red before the merge: a person decides (the red pipeline is a row of its own)");
        }
        // A merge that turned the integration branch red is fixed forward, as a developer does: the problem goes back to staging, where an agent brings the branch back to green, and its lot waits (ADR 0055)
        if (world == World.Git && failures.Contains(PreconditionFailure.IntegrationBranchRedAfterMerge))
        {
            return (Route.FixForward, "the merge turned the integration branch red: the problem goes back to the staging world to bring it back to green");
        }
        // A release that the controller judged failed never stays in the release world (its mandate could only deploy the same commit again): the problem goes back to the first world, whose mandate is issued
        // like the first one of a problem, so a certificate that backs no more mandates or a loop at its bound refuse there with their own reason. A pipeline result that is not known keeps its answer.
        if (repairsByRestart)
        {
            return (Route.BackToFirstWorld, "the release did not verify: the problem goes back to the first world");
        }
        return (Route.SendBackInPlace, "a check of the code failed: the mission is sent back");
    }

    private async Task<ConductResult> RouteFailuresAsync(Mission mission, ProblemRun run, IReadOnlyList<PreconditionFailure> failures, CancellationToken cancellationToken, bool controllerJudgedFailed = false)
    {
        var (route, why) = RouteOf(mission.World, failures, controllerJudgedFailed, run.Approval.Certificate.Kind == CertificateKind.Rule);
        return route switch
        {
            Route.Escalate => Moved(await EscalateAsync(run, mission, why), "escalated to the maintainer", failures),
            Route.Stop => Moved(await EndAsync(run, mission, MissionEvent.EndedByController, why), $"{why}: the work stops", failures),
            Route.BackToFirstWorld => await BackToFirstWorldAsync(mission, run, failures, cancellationToken),
            Route.FixForward => await FixForwardAsync(mission, run, failures),
            _ => Moved(await SendBackAsync(mission, run), why, failures),
        };
    }

    // An answer where a check failed and the mission moved: not Ok, the mission that came out of it, the failures that decided
    private static ConductResult Moved(ConductResult inner, string refusal, IReadOnlyList<PreconditionFailure> failures) =>
        inner.Ok ? new ConductResult(false, refusal, failures, inner.Mission, inner.Mandate) : inner;

    // The controller judged that the release does not verify (a send-back in the release world): the facts of the control plane are read all the same and the one table routes them with that judgement
    private async Task<ConductResult> ReleaseJudgedFailedAsync(Mission mission, ProblemRun run, CancellationToken cancellationToken) =>
        await RouteFailuresAsync(mission, run, await ClosingChecksAsync(run, mission, dossiers.Get(mission.Id), cancellationToken), cancellationToken, controllerJudgedFailed: true);

    // The release did not verify, said by the facts of the control plane (a pipeline that failed) or by the controller's judgement of the dossier (the verification of the release does not hold): the mission
    // ends and the problem goes back to the first world (a staging if the application has one, else the Git world). A release that failed under a standing rule is a person's: nobody approved this problem
    private async Task<ConductResult> BackToFirstWorldAsync(Mission mission, ProblemRun run, IReadOnlyList<PreconditionFailure> failures, CancellationToken cancellationToken)
    {
        // The power is read again here, as at every crossing: a grant or a certificate withdrawn while the release ran stops the problem, and the first world must be within what the powers allow
        var first = run.UsesStaging ? World.Staging : World.Git;
        if (HighestNow(run.Entry, run.Approval) is not { } highest || first > highest)
        {
            return await EndAsync(run, mission, MissionEvent.EndedByController, "the powers of the problem no longer reach the first world, or the grant was withdrawn");
        }
        if (first == World.Git && await GitFrameFailuresAsync(run, cancellationToken) is { Count: > 0 } frame)
        {
            return Moved(await EndAsync(run, mission, MissionEvent.EndedByController, "the merge could deploy beyond the approval, or the agent could create tags or reach a write credential"), "the frame of the Git world no longer holds: the work stops", frame);
        }
        var transition = MissionMachine.Apply(mission, MissionEvent.EndedByController, options.Loop, reason: "the release did not verify: the problem goes back to the first world");
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
        await BestEffortAsync(MissionAuditEvents.MissionEnded, mission.Id, Text($"the release did not verify ({string.Join(", ", failures)}): back to the first world"));
        // The problem touches production now: it goes ahead of the normal ones for the next free machine (a hot lot, ADR 0050). Its mandate is not wider for it: the priority orders the work, it grants no power
        var priority = FlowPriority.Raised(run.Priority);
        var opened = await OpenAsync(run with { CurrentWorld = first, Priority = priority }, first, MandateSource.Controller);
        // The raised priority is kept with the problem whether or not a mandate could be issued (the run is saved either way): the trail says it after that step, so it never claims a raise that was not stored
        await BestEffortAsync(MissionAuditEvents.PriorityRaised, run.ProblemId, Text($"the release did not verify: priority {run.Priority} to {priority}"));
        // The mission ended whatever Open says: when no mandate can be issued the problem failed, and the answer still carries the mission that moved
        return opened.Ok ? Moved(opened, "the release did not verify: the problem goes back to the first world", failures) : new ConductResult(false, opened.Refusal, opened.Failures, ended, null);
    }
}
