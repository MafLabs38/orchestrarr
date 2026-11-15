using System.Globalization;

namespace Orchestrarr.Core.Missions;

/// <summary>The settings of the conductor: how long a mandate lasts, which paths no mission may touch, the bounds of the loop.</summary>
public sealed record ConductorOptions(TimeSpan MandateLifetime, IReadOnlyList<string> ForbiddenPaths, LoopPolicy Loop)
{
    public static ConductorOptions Default { get; } = new(TimeSpan.FromMinutes(60), [".github/**", "**/*.enc.*", "**/.env*"], LoopPolicy.Default);
}

/// <summary>
/// What an operation of the conductor gives back: the new state of the mission, the mandate issued if any, or why it was refused. **Ok false with no mission: nothing happened. Ok false with a mission:
/// a check of the code failed and the mission moved (it was sent back, escalated or ended), the stores were written.**
/// </summary>
public sealed record ConductResult(bool Ok, string? Refusal, IReadOnlyList<PreconditionFailure> Failures, Mission? Mission, SignedMandate? Mandate)
{
    /// <summary>Whether the refusal changed something: the mission moved even though a check failed.</summary>
    public bool Moved => !Ok && Mission is not null;

    public static ConductResult Fail(string refusal, IReadOnlyList<PreconditionFailure>? failures = null) => new(false, refusal, failures ?? [], null, null);

    public static ConductResult Success(Mission mission, SignedMandate? mandate = null) => new(true, null, [], mission, mandate);
}

/// <summary>
/// Conducts a problem through the worlds (FUNCTIONAL 4, ADR 0030 to 0032, 0036): it issues the first mandate from the plan **on a proved approval**, applies what the controller decides, issues
/// the next mandate only after the checks of the code, bounds the loop, and ends everything cleanly on a stop, an expiry or a budget. It holds no agent and starts nothing: runners and agents
/// are reached through mandates. Every refusal says why, nothing the controller says can waive a check of the code, **every operation on a problem is serialised**, and **every mandate, decision and
/// end is written to the audit trail**.
/// </summary>
public sealed class ProblemConductor(
    MandateSigner signer, MissionStores stores, PowerSources powers, IFactsSource facts, IRunnerDirectory runners, IMissionAudit audit, TimeProvider time, ConductorOptions options, Func<string> newId, ProblemLocks? problemLocks = null)
{
    private readonly IMissionStore missions = stores.Missions;
    private readonly IMandateLedger mandates = stores.Mandates;
    private readonly IProblemStore problems = stores.Problems;
    private readonly IDossierStore dossiers = stores.Dossiers;
    private readonly IApprovalSource approvals = powers.Approvals;
    private readonly ApprovalVerifier approvalVerifier = powers.Verifier;
    private readonly IApprovalWithdrawals withdrawals = powers.Withdrawals;
    private readonly IGrantSource grants = powers.Grants;
    private readonly IStopSignal stop = powers.Stop;
    private readonly ProblemLocks _locks = problemLocks ?? new ProblemLocks();

    /// <summary>The acts a world lists in its mandates: the ones that have consequences outside the workspace.</summary>
    public static IReadOnlyList<GatedAct> ActsOf(World world) => WorldActs.Of(world);

    /// <summary>
    /// The highest world the powers reach **now**: the lowest of what the plan entry asked, what the maintainer's certificate says and the grant of the application read at this moment (D-10, D-11, D-53). Null: no power.
    /// </summary>
    private World? HighestNow(PlanEntry entry, SignedApproval approval)
    {
        // A certificate the authority withdrew is no power at all, for the crossings, the acts and the sweep alike (a list that cannot be read counts as withdrawn)
        if (withdrawals.IsWithdrawn(approval.Certificate.Id))
        {
            return null;
        }
        var approved = entry.MaxWorld < approval.Certificate.MaxWorld ? entry.MaxWorld : approval.Certificate.MaxWorld;
        return EffectivePower.Of(approved, grants.MaxWorldFor(entry.ApplicationId));
    }

    // The certificate must say at least what the entry asks: the repositories, the budget
    private static bool Covers(SignedApproval approval, PlanEntry entry) =>
        string.Equals(approval.Certificate.ApplicationId, entry.ApplicationId, StringComparison.Ordinal)
        && (approval.Certificate.Kind == CertificateKind.Problem ? string.Equals(approval.Certificate.Subject, entry.ProblemId, StringComparison.Ordinal) : string.Equals(approval.Certificate.Subject, entry.ProblemClass, StringComparison.Ordinal))
        && entry.Repositories.All(repository => approval.Certificate.Repositories.Contains(repository, StringComparer.Ordinal))
        && entry.Budget.MaxMinutes <= approval.Certificate.Budget.MaxMinutes && entry.Budget.MaxAttempts <= approval.Certificate.Budget.MaxAttempts && entry.Budget.MaxTurns <= approval.Certificate.Budget.MaxTurns;

    /// <summary>Starts a problem that is in the plan: the stop, the proved approval, the staging gate, the powers, then the first mandate in the first world.</summary>
    public ConductResult Start(PlanEntry entry, StagingDeclaration declaration, StagingRequirement requirement, StagingState state)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Locked(entry.ProblemId, () =>
        {
            if (stop.Engaged)
            {
                return ConductResult.Fail("the global stop is engaged");
            }
            var previous = problems.Get(entry.ProblemId);
            if (previous is { State: ProblemState.InProgress or ProblemState.Escalated })
            {
                return ConductResult.Fail("the problem is already being worked on");
            }
            if (approvals.For(entry) is not { } approval)
            {
                return ConductResult.Fail("the maintainer's approval of this plan entry is not proved");
            }
            // An approval is for one run: a problem that was stopped, failed or closed is started again only on a new approval (a stop is not undone by calling Start again, nor an old approval brought back after a newer one)
            if ((previous is not null && string.Equals(previous.Approval.Certificate.Id, approval.Certificate.Id, StringComparison.Ordinal))
                || (approval.Certificate.Kind == CertificateKind.Problem && mandates.CountForApproval(approval.Certificate.Id) > 0))
            {
                return ConductResult.Fail("this approval was already used by this problem: the maintainer approves again");
            }
            if (!Covers(approval, entry))
            {
                return ConductResult.Fail("the certificate of the approval does not cover this plan entry");
            }
            var verdict = StagingGate.Decide(declaration, requirement, state);
            if (verdict is not (StagingVerdict.Usable or StagingVerdict.UsableWithNoNet))
            {
                return ConductResult.Fail($"the staging is not usable: {verdict}", [PreconditionFailure.StagingNotUsable]);
            }
            var usesStaging = verdict == StagingVerdict.Usable;
            var first = usesStaging ? World.Staging : World.Git;
            var byRule = approval.Certificate.Kind == CertificateKind.Rule;
            if (HighestNow(entry, approval) is not { } highest || first > highest)
            {
                return ConductResult.Fail("the powers given do not reach the first world: the grant is off until the maintainer enables it");
            }
            if (!Required(MissionAuditEvents.ProblemStarted, entry.ProblemId, Text($"application {entry.ApplicationId}, validated by {entry.Validation.By}, up to {highest}, staging {(usesStaging ? "used" : "declared none")}")))
            {
                return ConductResult.Fail("the audit trail cannot be written: no power is granted");
            }
            // The run is kept by Open once the first mandate exists: a start that fails leaves nothing in progress
            var run = new ProblemRun(entry.ProblemId, entry.ApplicationId, ProblemState.InProgress, time.GetUtcNow(), entry, highest, usesStaging, approval, first);
            if (first == World.Git)
            {
                // An application with no staging starts in the Git world: the frame of the merge is checked here too, since no crossing will do it (the diff, the CI and the executed files are checked at the merge itself)
                var frame = CheckFacts(run, World.Git, previousDossierExists: true, dossier: null, commit: null, issuing: false)
                    .Where(failure => failure is PreconditionFailure.MergeMayDeployBeyondApproval or PreconditionFailure.AgentCouldCreateTags or PreconditionFailure.AgentBranchCouldReachWriteCredentials).ToList();
                if (frame.Count > 0)
                {
                    return ConductResult.Fail("the merge could deploy beyond the approval, or the agent could create tags or reach a write credential", frame);
                }
            }
            return Open(run, first, byRule ? MandateSource.StandingRule : MandateSource.PlanValidation);
        });
    }

    /// <summary>The runner accepted the mandate and the worker started.</summary>
    public ConductResult Started(string missionId) => OnMission(missionId, mission => Move(mission, MissionEvent.Started));

    /// <summary>The worker claims it is done: the dossier is kept as an untrusted claim and the mission waits for the controller.</summary>
    public ConductResult HandOver(string missionId, Dossier dossier)
    {
        ArgumentNullException.ThrowIfNull(dossier);
        if (!string.Equals(dossier.MissionId, missionId, StringComparison.Ordinal))
        {
            return ConductResult.Fail("the dossier is for another mission");
        }
        return OnMission(missionId, mission =>
        {
            // The dossier is kept first: a mission never waits for a decision with no dossier
            if (!MissionMachine.Apply(mission, MissionEvent.HandedOver, options.Loop).Allowed)
            {
                return Move(mission, MissionEvent.HandedOver);
            }
            dossiers.Save(dossier);
            var result = Move(mission, MissionEvent.HandedOver);
            if (result.Ok)
            {
                BestEffort(MissionAuditEvents.MissionHandedOver, missionId, Text($"complex: {dossier.Complex}"));
            }
            return result;
        });
    }

    /// <summary>Applies the controller's decision on a mission that handed over.</summary>
    public ConductResult Decide(string missionId, ControllerDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return OnMission(missionId, mission =>
        {
            if (mission.State != MissionState.HandingOver)
            {
                return ConductResult.Fail("only a mission that handed over can be decided");
            }
            if (!decision.IsWellFormed)
            {
                return ConductResult.Fail("a decision needs its reasons, and a send-back its remarks");
            }
            if (problems.Get(mission.ProblemId) is not { } run)
            {
                return ConductResult.Fail("the problem of this mission is unknown");
            }
            var decided = Required(MissionAuditEvents.ControllerDecided, missionId, Text($"{decision.Kind}: {string.Join(" | ", decision.Reasons)}"));
            if (!decided && decision.Kind == ControllerDecisionKind.Grant)
            {
                return ConductResult.Fail("the audit trail cannot be written: no power is granted");
            }
            return decision.Kind switch
            {
                ControllerDecisionKind.Grant => Grant(run, mission, decision),
                ControllerDecisionKind.SendBack => SendBack(mission, run),
                ControllerDecisionKind.End => End(run, mission, MissionEvent.EndedByController, decision.Reasons[0]),
                _ => Escalate(run, mission, decision.Reasons[0]),
            };
        });
    }

    /// <summary>The global stop, the maintainer or the budget: every mandate of the problem is revoked and the running mission ends cleanly.</summary>
    public ConductResult Stop(string problemId, string reason) => Locked(problemId, () => StopLocked(problemId, reason));

    /// <summary>The mission used up its budget: it ends as failed and the problem fails with it; a loop never goes on past a budget.</summary>
    public ConductResult BudgetExhausted(string missionId) =>
        OnMission(missionId, mission => problems.Get(mission.ProblemId) is { } run ? End(run, mission, MissionEvent.BudgetExhausted, "the budget is used up") : ConductResult.Fail("the problem of this mission is unknown"));

    /// <summary>
    /// What no runner will ever say for itself: with the global stop engaged every problem in progress is stopped; a mandate that expired while its mission had not handed over ends it (a runner
    /// that disappeared would otherwise leave the problem in progress for ever); a problem that has run for longer than its bound is stopped. Returns how many problems were ended.
    /// </summary>
    public int Sweep()
    {
        var ended = 0;
        foreach (var run in problems.InProgress())
        {
            var reason = SweepReason(run);
            if (reason is not null && Stop(run.ProblemId, reason).Ok)
            {
                ended++;
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
        return expired ? "the mandate expired" : null;
    }

    private ConductResult StopLocked(string problemId, string reason)
    {
        if (problems.Get(problemId) is not { State: ProblemState.InProgress } run)
        {
            return ConductResult.Fail("the problem is not being worked on");
        }
        Mission? last = null;
        foreach (var mission in missions.ForProblem(problemId))
        {
            Revoke(mission);
            if (!mission.IsOver && MissionMachine.Apply(mission, MissionEvent.Stopped, options.Loop, reason: reason) is { Mission: { } ended })
            {
                missions.Save(ended);
                BestEffort(MissionAuditEvents.MissionEnded, mission.Id, Text($"stopped: {reason}"));
                last = ended;
            }
        }
        problems.Save(run with { State = ProblemState.Failed, Note = reason });
        BestEffort(MissionAuditEvents.ProblemFailed, problemId, reason);
        // The problem moved to Failed even when no mission was running: that is a stop that worked
        return new ConductResult(true, null, [], last, null);
    }

    private ConductResult Grant(ProblemRun run, Mission mission, ControllerDecision decision)
    {
        var dossier = dossiers.Get(mission.Id);
        var complex = dossier?.Complex ?? false;
        if (mission.World == World.Release)
        {
            // The last world: the controller closes the problem on facts (FR-CTL-9), after the same checks
            var closing = ClosingChecks(run, mission, dossier);
            return closing.Count > 0 ? SendBackWithFailures(mission, run, closing) : Close(run, mission, complex, null, "release verified", null);
        }
        // The power is read again here, at every crossing: a grant withdrawn since the start stops the problem (the grant of an application is the maintainer's to give and to take back)
        if (HighestNow(run.Entry, run.Approval) is not { } highest)
        {
            return End(run, mission, MissionEvent.EndedByController, "the grant of the application was withdrawn");
        }
        var next = mission.World + 1;
        if (next > highest)
        {
            // The powers given stop here: the problem is done up to the world its powers allow, and says so (a plan validated up to the Git world ends there)
            var checks = ClosingChecks(run, mission, dossier);
            return checks.Count > 0
                ? SendBackWithFailures(mission, run, checks)
                : Close(run, mission, complex, $"done up to the {highest} world, the highest that its powers allow", Text($"highest world reached: {highest}"), "done up to the highest world its powers allow");
        }
        var failures = CheckFacts(run, next, previousDossierExists: dossier is not null, dossier, commit: dossier?.Commit, issuing: true);
        if (!ControllerLimits.MayGrant(decision, next, highest, failures))
        {
            return failures.Count > 0 ? SendBackWithFailures(mission, run, failures) : ConductResult.Fail("the controller may not grant a world that the plan does not allow");
        }
        var accepted = MissionMachine.Apply(mission, MissionEvent.Granted, options.Loop, complex: complex).Mission ?? mission;
        missions.Save(accepted);
        // The mandate of an accepted mission is withdrawn at once: the power never outlives the work
        Revoke(mission);
        BestEffort(MissionAuditEvents.MissionAccepted, mission.Id, Text($"next world {next}"));
        return Open(run with { CurrentWorld = next }, next, MandateSource.Controller);
    }

    // The checks to close a problem in the world of the mission: the facts of that world on the commit the worker claims, with no new mandate to issue
    private List<PreconditionFailure> ClosingChecks(ProblemRun run, Mission mission, Dossier? dossier) =>
        CheckFacts(run, mission.World, previousDossierExists: dossier is not null, dossier, commit: dossier?.Commit, issuing: false);

    // The mission is accepted and the problem is closed: the mandate is revoked, the problem says how far it went
    private ConductResult Close(ProblemRun run, Mission mission, bool complex, string? problemNote, string acceptedDetail, string? closedDetail)
    {
        var closed = MissionMachine.Apply(mission, MissionEvent.Granted, options.Loop, complex: complex).Mission ?? mission;
        missions.Save(closed);
        Revoke(mission);
        problems.Save(run with { State = ProblemState.Closed, CurrentWorld = null, Note = problemNote });
        BestEffort(MissionAuditEvents.MissionAccepted, mission.Id, acceptedDetail);
        BestEffort(MissionAuditEvents.ProblemClosed, run.ProblemId, closedDetail);
        return ConductResult.Success(closed);
    }

    /// <summary>
    /// The **act gateway** (ARCHITECTURE section 5): before a merge or a deployment is performed, the checks of the code are made again **now, on the exact commit that is about to be merged or deployed**, with the
    /// power read at this moment. A mandate says which acts may be asked for; this says whether the act may be done now. Nothing is performed here: the control plane's gateway does the act when this allows it.
    /// </summary>
    public ActAuthorisation AuthoriseAct(string missionId, GatedAct act, string? commit)
    {
        if (missions.Get(missionId) is not { } first)
        {
            return ActAuthorisation.Refused("unknown mission");
        }
        return Locked(first.ProblemId, () =>
        {
            if (missions.Get(missionId) is not { State: MissionState.Mandated or MissionState.Running or MissionState.HandingOver } mission || mission.MandateId is not { } mandateId
                || mandates.Get(mandateId) is not { } signed || mandates.IsRevoked(mandateId))
            {
                return ActAuthorisation.Refused("the mission has no mandate in force");
            }
            if (time.GetUtcNow() >= signed.Mandate.ExpiresAt || !signed.Mandate.Acts.Contains(act))
            {
                return ActAuthorisation.Refused("the mandate has expired or does not list this act");
            }
            if (stop.Engaged)
            {
                return ActAuthorisation.Refused("the global stop is engaged");
            }
            if (problems.Get(mission.ProblemId) is not { State: ProblemState.InProgress } run || HighestNow(run.Entry, run.Approval) is not { } highest || mission.World > highest)
            {
                return ActAuthorisation.Refused("the powers of the problem no longer reach this world, or the certificate was withdrawn");
            }
            // What was stored is verified again, not trusted: the mandate is the control plane's own signature and falls inside the certificate of the authority
            if (!signer.Verify(signed) || approvalVerifier.Check(run.Approval, signed.Mandate) != ApprovalRefusal.None)
            {
                return ActAuthorisation.Refused("the mandate or its certificate does not verify");
            }
            // The acts that change what runs (a merge, a deployment) need the facts of this moment; the other acts only need the mandate
            var failures = act is GatedAct.MergePullRequest or GatedAct.Deploy
                ? CheckFacts(run, mission.World, previousDossierExists: PreviousDossierExists(run, mission), LatestDossier(run, mission), commit, issuing: false)
                : [];
            if (failures.Count > 0)
            {
                BestEffort(MissionAuditEvents.ActRefused, mission.Id, Text($"{act} on {commit ?? "an unknown commit"}: {string.Join(", ", failures)}"));
                if (failures.Contains(PreconditionFailure.ExecutedFilesTouchedWhereMergeDeploys))
                {
                    _ = Escalate(run, mission, "the change touches files that the pipeline executes, and the merge may deploy: a person decides", MissionEvent.Stopped);
                }
                return ActAuthorisation.Refused("a check of the code failed", failures);
            }
            return Required(MissionAuditEvents.ActAuthorised, mission.Id, Text($"{act} on {commit ?? "no commit"}"))
                ? new ActAuthorisation(true, null, [])
                : ActAuthorisation.Refused("the audit trail cannot be written: no power is granted");
        });
    }

    // Whether the world before this one left a dossier, which the mission of a world needs (a problem with no staging has none to show)
    private bool PreviousDossierExists(ProblemRun run, Mission mission) =>
        mission.World == World.Staging || (mission.World == World.Git && !run.UsesStaging) || LatestDossier(run, mission) is not null;

    private Dossier? LatestDossier(ProblemRun run, Mission mission) =>
        missions.ForProblem(run.ProblemId).Where(other => other.World < mission.World).OrderByDescending(other => other.World).Select(other => dossiers.Get(other.Id)).FirstOrDefault(found => found is not null);

    // The facts come from the control plane's own source; the plan, the stop, the loop and the staging are the conductor's own: none is taken from the worker
    // `issuing` is true only where a new mandate is about to be issued: the bound of the loop guards a new mandate, not the acts of one that is already in force nor the closing of the problem
    private List<PreconditionFailure> CheckFacts(ProblemRun run, World world, bool previousDossierExists, Dossier? dossier, string? commit, bool issuing)
    {
        var observed = facts.Facts(run.ProblemId, world, dossier, commit);
        // The commit the facts are about is the head that the source read itself; the worker's pointer is only a claim, and a claim that does not match (or that cannot be matched) fails the check
        var claimMatches = observed.WorkerClaimMatchesFacts && (commit is null || (observed.HeadCommit is not null && string.Equals(observed.HeadCommit, commit, StringComparison.Ordinal)));
        // The ceiling of the merge is the power in effect now (the lowest of the entry, the certificate and the grant read at this moment), never the certificate alone; no power at all is the lowest world
        var ceiling = HighestNow(run.Entry, run.Approval) ?? World.Staging;
        var checks = new PreconditionFacts(
            world, PlanCovers: true, observed.BudgetAvailable, stop.Engaged, run.UsesStaging ? StagingVerdict.Usable : StagingVerdict.UsableWithNoNet, previousDossierExists,
            observed.DiffIsClean, observed.CiGreenOnExactCommit, observed.ArtifactIsTheOneProvedInStaging, claimMatches,
            issuing ? options.Loop.Check(mandates.CountForProblem(run.ProblemId), time.GetUtcNow() - run.StartedAt) : LoopVerdict.Allowed, observed.WayBackKnown,
            ApprovedMaxWorld: ceiling, observed.MergeMayDeploy, observed.ExecutedFilesTouched, observed.TagCreationBlocked, observed.BranchCannotReachWriteCredentials);
        // The release of an application with a staging needs the artifact that was proved there (FR-WLD-14)
        return [.. MandatePreconditions.Check(checks, requiresStagingProof: run.UsesStaging && world == World.Release)];
    }

    private ConductResult SendBackWithFailures(Mission mission, ProblemRun run, IReadOnlyList<PreconditionFailure> failures)
    {
        // A change that touches what the pipeline executes where a merge may deploy is for a person to decide: it is escalated, not sent back
        if (failures.Contains(PreconditionFailure.ExecutedFilesTouchedWhereMergeDeploys))
        {
            var escalated = Escalate(run, mission, "the change touches files that the pipeline executes, and the merge may deploy: a person decides");
            return escalated.Ok ? new ConductResult(false, "escalated to the maintainer", failures, escalated.Mission, null) : escalated;
        }
        // A limit of the loop or the global stop cannot be repaired by sending the mission back: the work stops
        if (failures.Contains(PreconditionFailure.LoopLimitReached) || failures.Contains(PreconditionFailure.KillSwitchEngaged))
        {
            var why = failures.Contains(PreconditionFailure.KillSwitchEngaged) ? "the global stop is engaged" : "the loop reached its limit";
            var ended = End(run, mission, MissionEvent.EndedByController, why);
            return ended.Ok ? new ConductResult(false, $"{why}: the work stops", failures, ended.Mission, null) : ended;
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
        BestEffort(MissionAuditEvents.MissionSentBack, mission.Id, Text($"send-back {sent.SendBacks}"));
        if (sent.State == MissionState.Ended)
        {
            Revoke(mission);
            problems.Save(run with { State = ProblemState.Failed, Note = sent.EndReason });
            BestEffort(MissionAuditEvents.ProblemFailed, run.ProblemId, sent.EndReason);
        }
        return ConductResult.Success(sent);
    }

    // The controller escalated: a decision for the maintainer. The mission ends, its mandate is revoked, and the problem waits as Escalated, told, never as a failure.
    private ConductResult Escalate(ProblemRun run, Mission mission, string reason, MissionEvent ending = MissionEvent.EndedByController)
    {
        var transition = MissionMachine.Apply(mission, ending, options.Loop, reason: $"escalated to the maintainer: {reason}");
        if (transition.Mission is not { } ended)
        {
            return ConductResult.Fail(transition.Refusal ?? "refused");
        }
        missions.Save(ended);
        Revoke(mission);
        problems.Save(run with { State = ProblemState.Escalated, Note = reason });
        BestEffort(MissionAuditEvents.MissionEnded, mission.Id, Text($"escalated: {reason}"));
        BestEffort(MissionAuditEvents.ProblemEscalated, run.ProblemId, reason);
        return ConductResult.Success(ended);
    }

    private ConductResult End(ProblemRun run, Mission mission, MissionEvent ending, string reason)
    {
        var transition = MissionMachine.Apply(mission, ending, options.Loop, reason: reason);
        if (transition.Mission is not { } ended)
        {
            return ConductResult.Fail(transition.Refusal ?? "refused");
        }
        missions.Save(ended);
        Revoke(mission);
        problems.Save(run with { State = ProblemState.Failed, Note = reason });
        BestEffort(MissionAuditEvents.MissionEnded, mission.Id, Text($"{ended.End}: {reason}"));
        BestEffort(MissionAuditEvents.ProblemFailed, run.ProblemId, reason);
        return ConductResult.Success(ended);
    }

    private ConductResult Move(Mission mission, MissionEvent missionEvent)
    {
        var transition = MissionMachine.Apply(mission, missionEvent, options.Loop);
        if (transition.Mission is not { } moved)
        {
            return ConductResult.Fail(transition.Refusal ?? "refused");
        }
        missions.Save(moved);
        return ConductResult.Success(moved);
    }

    private void Revoke(Mission mission)
    {
        if (mission.MandateId is { } id && !mandates.IsRevoked(id))
        {
            mandates.Revoke(id);
            BestEffort(MissionAuditEvents.MandateRevoked, id, Text($"mission {mission.Id}"));
        }
    }

    // A new mission in a world, and the mandate for it: signed, recorded, counted, audited. The stop and the loop are checked here too, not only at a grant.
    private ConductResult Open(ProblemRun run, World world, MandateSource source)
    {
        if (stop.Engaged)
        {
            return Failed(run, "the global stop is engaged", [PreconditionFailure.KillSwitchEngaged]);
        }
        var entry = run.Entry;
        var loop = options.Loop.Check(mandates.CountForProblem(run.ProblemId), time.GetUtcNow() - run.StartedAt);
        if (loop != LoopVerdict.Allowed)
        {
            return Failed(run, $"the loop reached its limit: {loop}", [PreconditionFailure.LoopLimitReached]);
        }
        if (mandates.CountForApproval(run.Approval.Certificate.Id) >= run.Approval.Certificate.MaxMandates)
        {
            return Failed(run, "the certificate of the approval backs no more mandates", [PreconditionFailure.LoopLimitReached]);
        }
        var frame = new MissionFrame(entry.Repositories, [.. options.ForbiddenPaths.Union(run.Approval.Certificate.ForbiddenPaths, StringComparer.Ordinal)], $"{world}:{entry.ApplicationId}");
        var mission = new Mission(newId(), run.ProblemId, run.ApplicationId, world, ObjectiveOf(world, entry), frame, entry.Budget, MissionState.Proposed);
        var now = time.GetUtcNow();
        // A mandate never outlives the approval it falls inside
        var expires = now + options.MandateLifetime < run.Approval.Certificate.ExpiresAt ? now + options.MandateLifetime : run.Approval.Certificate.ExpiresAt;
        var mandate = new Mandate(newId(), mission.Id, run.ProblemId, world, runners.RunnerFor(world, run.ApplicationId), ActsOf(world), frame, entry.Budget, now, expires, source, newId(), run.Approval.Certificate.Id, entry.ApplicationId, entry.ProblemClass);
        var covered = approvalVerifier.Check(run.Approval, mandate);
        if (covered != ApprovalRefusal.None)
        {
            return Failed(run, $"the mandate does not fall inside the approval: {covered}");
        }
        // The mandate is written to the audit trail before it exists: a power is never granted on an entry that cannot be written
        if (!Required(MissionAuditEvents.MandateIssued, mandate.Id, Text($"{world} for {mandate.RunnerId}, {source}, until {mandate.ExpiresAt:u}, acts {string.Join(",", mandate.Acts)}")))
        {
            return Failed(run, "the audit trail cannot be written: no power is granted");
        }
        var signed = signer.Sign(mandate, run.Approval);
        mandates.Record(signed);
        var mandated = MissionMachine.Apply(mission, MissionEvent.MandateIssued, options.Loop, mandateId: mandate.Id).Mission ?? mission;
        missions.Save(mandated);
        problems.Save(run with { CurrentWorld = world });
        return ConductResult.Success(mandated, signed);
    }

    // A mandate that cannot be issued ends the problem as failed, with the reason and an entry in the trail: nothing is left in progress with no mission and no way forward
    private ConductResult Failed(ProblemRun run, string reason, IReadOnlyList<PreconditionFailure>? failures = null)
    {
        problems.Save(run with { State = ProblemState.Failed, Note = reason });
        BestEffort(MissionAuditEvents.ProblemFailed, run.ProblemId, reason);
        return ConductResult.Fail(reason, failures);
    }

    // Every operation on a problem runs alone: two calls that overlap cannot both pass the same check of the state and issue two mandates
    private ConductResult OnMission(string missionId, Func<Mission, ConductResult> work)
    {
        if (missions.Get(missionId) is not { } first)
        {
            return ConductResult.Fail("unknown mission");
        }
        return Locked(first.ProblemId, () => missions.Get(missionId) is { } mission ? work(mission) : ConductResult.Fail("unknown mission"));
    }

    private T Locked<T>(string problemId, Func<T> work)
    {
        lock (_locks.Of(problemId))
        {
            return work();
        }
    }

    // Granting a power is written first and refused when it cannot be written; withdrawing a power never waits for the trail
    private bool Required(string eventName, string subject, string? detail) => audit.Record(eventName, subject, detail);

    private void BestEffort(string eventName, string subject, string? detail) => _ = audit.Record(eventName, subject, detail);

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string ObjectiveOf(World world, PlanEntry entry) => world switch
    {
        World.Staging => entry.Objective,
        World.Git => $"The change for \"{entry.Objective}\" is merged into the integration branch with every pipeline green.",
        _ => $"The change for \"{entry.Objective}\" runs in production and is verified.",
    };
}
