namespace Orchestrarr.Core.Missions;

/// <summary>The settings of the conductor: how long a mandate lasts, which paths no mission may touch, the bounds of the loop.</summary>
public sealed record ConductorOptions(TimeSpan MandateLifetime, IReadOnlyList<string> ForbiddenPaths, LoopPolicy Loop)
{
    public static ConductorOptions Default { get; } = new(TimeSpan.FromMinutes(60), [".github/**", "**/*.enc.*", "**/.env*"], LoopPolicy.Default);
}

/// <summary>What an operation of the conductor gives back: the new state of the mission, the mandate issued if any, or why it was refused.</summary>
public sealed record ConductResult(bool Ok, string? Refusal, IReadOnlyList<PreconditionFailure> Failures, Mission? Mission, SignedMandate? Mandate)
{
    public static ConductResult Fail(string refusal, IReadOnlyList<PreconditionFailure>? failures = null) => new(false, refusal, failures ?? [], null, null);

    public static ConductResult Success(Mission mission, SignedMandate? mandate = null) => new(true, null, [], mission, mandate);
}

/// <summary>
/// Conducts a problem through the worlds (FUNCTIONAL 4, ADR 0030 to 0032, 0036): it issues the first mandate from the plan, applies what the controller decides, issues the next
/// mandate only after the checks of the code, bounds the loop, and ends everything cleanly on a stop. It holds no agent and starts nothing: runners and agents are reached
/// through mandates. Every refusal says why, and nothing the controller says can waive a check of the code.
/// </summary>
public sealed class ProblemConductor(
    MandateSigner signer, IMissionStore missions, IMandateLedger mandates, IProblemStore problems, IDossierStore dossiers, IFactsSource facts, IRunnerDirectory runners,
    TimeProvider time, ConductorOptions options, Func<string> newId)
{
    /// <summary>The acts a world lists in its mandates: the ones that have consequences outside the workspace.</summary>
    public static IReadOnlyList<GatedAct> ActsOf(World world) => world switch
    {
        World.Staging => [GatedAct.ReserveStaging, GatedAct.RestoreStaging, GatedAct.ReleaseStaging],
        World.Git => [GatedAct.OpenPullRequest, GatedAct.MergePullRequest],
        _ => [GatedAct.Deploy, GatedAct.Rollback],
    };

    /// <summary>Starts a problem that is in the plan: the staging gate, the powers, then the first mandate in the first world.</summary>
    public ConductResult Start(PlanEntry entry, StagingDeclaration declaration, StagingRequirement requirement, StagingState state, World? grant, World? rule)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (problems.Get(entry.ProblemId) is { State: ProblemState.InProgress })
        {
            return ConductResult.Fail("the problem is already being worked on");
        }
        var verdict = StagingGate.Decide(declaration, requirement, state);
        if (verdict is not (StagingVerdict.Usable or StagingVerdict.UsableWithNoNet))
        {
            return ConductResult.Fail($"the staging is not usable: {verdict}", [PreconditionFailure.StagingNotUsable]);
        }
        var usesStaging = verdict == StagingVerdict.Usable;
        var first = usesStaging ? World.Staging : World.Git;
        if (EffectivePower.Of(entry.MaxWorld, grant, rule) is not { } highest || first > highest)
        {
            return ConductResult.Fail("the powers given do not reach the first world");
        }
        var run = new ProblemRun(entry.ProblemId, entry.ApplicationId, ProblemState.InProgress, time.GetUtcNow(), entry, highest, usesStaging, first);
        problems.Save(run);
        var source = entry.Validation.RuleId is null ? MandateSource.PlanValidation : MandateSource.StandingRule;
        return Open(run, first, source);
    }

    /// <summary>The runner accepted the mandate and the worker started.</summary>
    public ConductResult Started(string missionId) => Move(missionId, MissionEvent.Started);

    /// <summary>The worker claims it is done: the dossier is kept as an untrusted claim and the mission waits for the controller.</summary>
    public ConductResult HandOver(string missionId, Dossier dossier)
    {
        ArgumentNullException.ThrowIfNull(dossier);
        if (!string.Equals(dossier.MissionId, missionId, StringComparison.Ordinal))
        {
            return ConductResult.Fail("the dossier is for another mission");
        }
        var result = Move(missionId, MissionEvent.HandedOver);
        if (result.Ok)
        {
            dossiers.Save(dossier);
        }
        return result;
    }

    /// <summary>Applies the controller's decision on a mission that handed over.</summary>
    public ConductResult Decide(string missionId, ControllerDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (missions.Get(missionId) is not { State: MissionState.HandingOver } mission)
        {
            return ConductResult.Fail("only a mission that handed over can be decided");
        }
        if (!decision.IsWellFormed)
        {
            return ConductResult.Fail("a decision needs its reasons, and a send-back its remarks");
        }
        var run = problems.Get(mission.ProblemId)!;
        return decision.Kind switch
        {
            ControllerDecisionKind.Grant => Grant(run, mission, decision),
            ControllerDecisionKind.SendBack => SendBack(mission, run),
            ControllerDecisionKind.End => End(run, mission, MissionEvent.EndedByController, ProblemState.Failed, decision.Reasons[0]),
            _ => End(run, mission, MissionEvent.EndedByController, ProblemState.Failed, $"escalated to the maintainer: {decision.Reasons[0]}"),
        };
    }

    /// <summary>The global stop, the maintainer or the budget: every mandate of the problem is revoked and the running mission ends cleanly.</summary>
    public ConductResult Stop(string problemId, string reason)
    {
        if (problems.Get(problemId) is not { State: ProblemState.InProgress } run)
        {
            return ConductResult.Fail("the problem is not being worked on");
        }
        Mission? last = null;
        foreach (var mission in missions.ForProblem(problemId))
        {
            mandates.Revoke(mission.MandateId ?? string.Empty);
            if (!mission.IsOver && MissionMachine.Apply(mission, MissionEvent.Stopped, options.Loop, reason: reason) is { Mission: { } ended })
            {
                missions.Save(ended);
                last = ended;
            }
        }
        problems.Save(run with { State = ProblemState.Failed, Note = reason });
        return last is null ? ConductResult.Fail("no mission was running") : ConductResult.Success(last);
    }

    /// <summary>The mission used up its budget: it ends as failed and the problem fails with it; a loop never goes on past a budget.</summary>
    public ConductResult BudgetExhausted(string missionId)
    {
        if (missions.Get(missionId) is not { } mission)
        {
            return ConductResult.Fail("unknown mission");
        }
        return End(problems.Get(mission.ProblemId)!, mission, MissionEvent.BudgetExhausted, ProblemState.Failed, "the budget is used up");
    }

    private ConductResult Grant(ProblemRun run, Mission mission, ControllerDecision decision)
    {
        var dossier = dossiers.Get(mission.Id);
        var complex = dossier?.Complex ?? false;
        if (mission.World == World.Release)
        {
            // The last world: the controller closes the problem on facts (FR-CTL-9), after the same checks
            var closing = CheckFacts(run, mission.World, dossier);
            if (closing.Count > 0)
            {
                return SendBackWithFailures(mission, run, closing);
            }
            var closed = MissionMachine.Apply(mission, MissionEvent.Granted, options.Loop, complex: complex).Mission!;
            missions.Save(closed);
            problems.Save(run with { State = ProblemState.Closed, CurrentWorld = null });
            return ConductResult.Success(closed);
        }
        var next = mission.World + 1;
        var failures = CheckFacts(run, next, dossier);
        if (!ControllerLimits.MayGrant(decision, next, run.HighestAllowed, failures))
        {
            return failures.Count > 0 ? SendBackWithFailures(mission, run, failures) : ConductResult.Fail("the controller may not grant a world that the plan does not allow");
        }
        var accepted = MissionMachine.Apply(mission, MissionEvent.Granted, options.Loop, complex: complex).Mission!;
        missions.Save(accepted);
        return Open(run with { CurrentWorld = next }, next, MandateSource.Controller);
    }

    // The facts come from the control plane's own source, and the loop is checked here: neither is taken from the worker
    private List<PreconditionFailure> CheckFacts(ProblemRun run, World world, Dossier? dossier)
    {
        var elapsed = time.GetUtcNow() - run.StartedAt;
        var observed = facts.Facts(run.ProblemId, world, dossier) with
        {
            World = world,
            Loop = options.Loop.Check(mandates.CountForProblem(run.ProblemId), elapsed),
            Staging = run.UsesStaging ? StagingVerdict.Usable : StagingVerdict.UsableWithNoNet,
        };
        // The release of an application with a staging needs the artifact that was proved there (FR-WLD-14)
        return [.. MandatePreconditions.Check(observed, requiresStagingProof: run.UsesStaging && world == World.Release)];
    }

    private ConductResult SendBackWithFailures(Mission mission, ProblemRun run, IReadOnlyList<PreconditionFailure> failures)
    {
        // A limit of the loop cannot be repaired by sending the mission back: the work stops and the maintainer is told
        if (failures.Contains(PreconditionFailure.LoopLimitReached))
        {
            var ended = End(run, mission, MissionEvent.EndedByController, ProblemState.Failed, "the loop reached its limit");
            return ended.Ok ? new ConductResult(false, "the loop reached its limit: the work stops", failures, ended.Mission, null) : ended;
        }
        var sent = SendBack(mission, run);
        return sent.Ok ? new ConductResult(false, "a check of the code failed: the mission is sent back", failures, sent.Mission, null) : sent;
    }

    private ConductResult SendBack(Mission mission, ProblemRun run)
    {
        var transition = MissionMachine.Apply(mission, MissionEvent.SentBack, options.Loop);
        if (transition.Mission is not { } sent)
        {
            return ConductResult.Fail(transition.Refusal ?? "refused");
        }
        missions.Save(sent);
        if (sent.State == MissionState.Ended)
        {
            problems.Save(run with { State = ProblemState.Failed, Note = sent.EndReason });
        }
        return ConductResult.Success(sent);
    }

    private ConductResult End(ProblemRun run, Mission mission, MissionEvent ending, ProblemState problemState, string reason)
    {
        var transition = MissionMachine.Apply(mission, ending, options.Loop, reason: reason);
        if (transition.Mission is not { } ended)
        {
            return ConductResult.Fail(transition.Refusal ?? "refused");
        }
        missions.Save(ended);
        mandates.Revoke(mission.MandateId ?? string.Empty);
        problems.Save(run with { State = problemState, Note = reason });
        return ConductResult.Success(ended);
    }

    private ConductResult Move(string missionId, MissionEvent missionEvent)
    {
        if (missions.Get(missionId) is not { } mission)
        {
            return ConductResult.Fail("unknown mission");
        }
        var transition = MissionMachine.Apply(mission, missionEvent, options.Loop);
        if (transition.Mission is not { } moved)
        {
            return ConductResult.Fail(transition.Refusal ?? "refused");
        }
        missions.Save(moved);
        return ConductResult.Success(moved);
    }

    // A new mission in a world, and the mandate for it: signed, recorded, counted
    private ConductResult Open(ProblemRun run, World world, MandateSource source)
    {
        var entry = run.Entry;
        var loop = options.Loop.Check(mandates.CountForProblem(run.ProblemId), time.GetUtcNow() - run.StartedAt);
        if (loop != LoopVerdict.Allowed)
        {
            problems.Save(run with { State = ProblemState.Failed, Note = $"the loop reached its limit: {loop}" });
            return ConductResult.Fail($"the loop reached its limit: {loop}", [PreconditionFailure.LoopLimitReached]);
        }
        var frame = new MissionFrame(entry.Repositories, options.ForbiddenPaths, $"{world}:{entry.ApplicationId}");
        var mission = new Mission(newId(), run.ProblemId, run.ApplicationId, world, ObjectiveOf(world, entry), frame, entry.Budget, MissionState.Proposed);
        var now = time.GetUtcNow();
        var mandate = new Mandate(newId(), mission.Id, run.ProblemId, world, runners.RunnerFor(world, run.ApplicationId), ActsOf(world), frame, entry.Budget, now, now + options.MandateLifetime, source, newId());
        var signed = signer.Sign(mandate);
        mandates.Record(signed);
        var mandated = MissionMachine.Apply(mission, MissionEvent.MandateIssued, options.Loop, mandateId: mandate.Id).Mission!;
        missions.Save(mandated);
        problems.Save(run with { CurrentWorld = world });
        return ConductResult.Success(mandated, signed);
    }

    private static string ObjectiveOf(World world, PlanEntry entry) => world switch
    {
        World.Staging => entry.Objective,
        World.Git => $"The change for \"{entry.Objective}\" is merged into the integration branch with every pipeline green.",
        _ => $"The change for \"{entry.Objective}\" runs in production and is verified.",
    };
}
