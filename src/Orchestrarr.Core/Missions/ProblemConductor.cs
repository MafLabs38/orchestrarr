using System.Globalization;

namespace Orchestrarr.Core.Missions;

/// <summary>The settings of the conductor: how long a mandate lasts, which paths no mission may touch, the bounds of the loop.</summary>
public sealed record ConductorOptions(TimeSpan MandateLifetime, IReadOnlyList<string> ForbiddenPaths, LoopPolicy Loop)
{
    public static ConductorOptions Default { get; } = new(TimeSpan.FromMinutes(60), ForbiddenPathsDefaults.Floor, LoopPolicy.Default);
}

/// <summary>
/// What an operation of the conductor gives back: the new state of the mission, the mandate issued if any, or why it was refused. **Ok false with no mission: nothing happened. Ok false with a mission:
/// a check of the code failed and the mission moved (it was sent back, escalated or ended), the stores were written; when the release did not verify the problem goes back to the first world and the
/// result carries the new mission **and its mandate**: a caller that dispatches on the mandate, not only on Ok, runs it.**
/// </summary>
public sealed record ConductResult(bool Ok, string? Refusal, IReadOnlyList<PreconditionFailure> Failures, Mission? Mission, SignedMandate? Mandate)
{
    /// <summary>Whether the refusal changed something: the mission moved even though a check failed.</summary>
    public bool Moved => !Ok && Mission is not null;

    public static ConductResult Fail(string refusal, IReadOnlyList<PreconditionFailure>? failures = null) => new(false, refusal, failures ?? [], null, null);

    public static ConductResult Success(Mission mission, SignedMandate? mandate = null) => new(true, null, [], mission, mandate);
}

/// <summary>What the conductor reaches outside itself: the facts it reads, the runners it names, the audit trail, the clock and the source of identifiers.</summary>
public sealed record ConductorServices(IFactsSource Facts, IRunnerDirectory Runners, IMissionAudit Audit, TimeProvider Time, Func<string> NewId);

/// <summary>
/// Conducts a problem through the worlds (FUNCTIONAL 4, ADR 0030 to 0032, 0036): it issues the first mandate from the plan **on a proved approval**, applies what the controller decides, issues
/// the next mandate only after the checks of the code, bounds the loop, and ends everything cleanly on a stop, an expiry or a budget. It holds no agent and starts nothing: runners and agents
/// are reached through mandates. Every refusal says why, nothing the controller says can waive a check of the code, **every operation on a problem is serialised**, and **every mandate, decision and
/// end is written to the audit trail**.
/// </summary>
public sealed partial class ProblemConductor(MandateSigner signer, MissionStores stores, PowerSources powers, ConductorServices services, ConductorOptions options, ProblemLocks? problemLocks = null)
{
    private readonly IFactsSource facts = services.Facts;
    private readonly IRunnerDirectory runners = services.Runners;
    private readonly IMissionAudit audit = services.Audit;
    private readonly TimeProvider time = services.Time;
    private readonly Func<string> newId = services.NewId;
    private readonly IMissionStore missions = stores.Missions;
    private readonly IMandateLedger mandates = stores.Mandates;
    private readonly IProblemStore problems = stores.Problems;
    private readonly IDossierStore dossiers = stores.Dossiers;
    private readonly IApprovalSource approvals = powers.Approvals;
    private readonly ApprovalVerifier approvalVerifier = powers.Verifier;
    private readonly IApprovalWithdrawals withdrawals = powers.Withdrawals;
    private readonly IGrantSource grants = powers.Grants;
    private readonly IStopSignal stop = powers.Stop;
    private readonly ProblemLocks locks = problemLocks ?? new ProblemLocks();

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
            if (!OneRepository.Is(entry.Repositories))
            {
                return ConductResult.Fail("a problem is on exactly one repository: a problem on several is as many problems (ADR 0041)");
            }
            var previous = problems.Get(entry.ProblemId);
            // An escalated problem waits for the maintainer: a new approval is his decision, and starts it again (the same approval does not: see below)
            if (previous is { State: ProblemState.InProgress })
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
                var frame = GitFrameFailures(run);
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

    private ConductResult Grant(ProblemRun run, Mission mission, ControllerDecision decision)
    {
        var dossier = dossiers.Get(mission.Id);
        var complex = dossier?.Complex ?? false;
        if (mission.World == World.Release)
        {
            // The last world: the controller closes the problem on facts (FR-CTL-9), after the same checks
            var closing = ClosingChecks(run, mission, dossier);
            return closing.Count > 0 ? ReleaseDidNotVerify(mission, run, closing) : Close(run, mission, complex, null, "release verified", null);
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
        var failures = CheckFacts(run, next, mission.Frame, mission.Id, previousDossierExists: dossier is not null, dossier, commit: dossier?.Commit, issuing: true);
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

    // The mission is accepted and the problem is closed: the mandate is revoked, the problem says how far it went
    private ConductResult Close(ProblemRun run, Mission mission, bool complex, string? problemNote, string acceptedDetail, string? closedDetail)
    {
        var closed = MissionMachine.Apply(mission, MissionEvent.Granted, options.Loop, complex: complex).Mission ?? mission;
        Settle(mission, closed, run with { State = ProblemState.Closed, CurrentWorld = null, Note = problemNote },
            new AuditLine(MissionAuditEvents.MissionAccepted, mission.Id, acceptedDetail), new AuditLine(MissionAuditEvents.ProblemClosed, run.ProblemId, closedDetail));
        return ConductResult.Success(closed);
    }

    // The frame of the merge in the Git world, read now: the merge that may deploy, the tags, a write credential, the identity of the agent. Where no crossing makes these checks (an application with no staging
    // starts in the Git world, and a problem that goes back to it is in the same case), the conductor makes them before it issues the mandate
    private List<PreconditionFailure> GitFrameFailures(ProblemRun run) =>
        CheckFacts(run, World.Git, FrameFor(run, "start"), missionId: null, previousDossierExists: true, dossier: null, commit: null, issuing: false)
            .Where(failure => failure is PreconditionFailure.MergeMayDeployUnderGitMandate or PreconditionFailure.AgentCouldCreateTags or PreconditionFailure.AgentBranchCouldReachWriteCredentials or PreconditionFailure.AgentIdentityNotConfined).ToList();

    // A release that does not verify is a failed push (D-93, ADR 0040): the mission ends, the problem stays in progress and the loop starts again in the first world (a new staging, then the Git world, then the
    // release), under the same approval, with the same budgets and without another validation. What cannot be repaired by going back (the stop, the limit of the loop, a change to what the pipeline executes)
    // keeps the answer it always had.
    private ConductResult ReleaseDidNotVerify(Mission mission, ProblemRun run, IReadOnlyList<PreconditionFailure> failures)
    {
        // Only what going back can repair sends the problem back: the pipelines that are not green and the artifact that is not the one proved. The other failures (the budget, the claim that does not match the
        // facts, the diff, the frame, the stop, the loop, the executed files) keep the answer they always had, because restarting would not change them
        if (!failures.All(failure => failure is PreconditionFailure.CiNotGreenOnExactCommit or PreconditionFailure.ArtifactNotProvedInStaging))
        {
            return SendBackWithFailures(mission, run, failures);
        }
        // The power is read again here, as at every crossing: a grant or a certificate withdrawn while the release ran stops the problem, and the first world must be within what the powers allow
        var first = run.UsesStaging ? World.Staging : World.Git;
        if (HighestNow(run.Entry, run.Approval) is not { } highest || first > highest)
        {
            return End(run, mission, MissionEvent.EndedByController, "the powers of the problem no longer reach the first world, or the grant was withdrawn");
        }
        if (first == World.Git && GitFrameFailures(run) is { Count: > 0 } frame)
        {
            var refused = End(run, mission, MissionEvent.EndedByController, "the merge could deploy beyond the approval, or the agent could create tags or reach a write credential");
            return refused.Ok ? new ConductResult(false, "the frame of the Git world no longer holds: the work stops", frame, refused.Mission, null) : refused;
        }
        var transition = MissionMachine.Apply(mission, MissionEvent.EndedByController, options.Loop, reason: "the release did not verify: the problem goes back to the first world");
        if (transition.Mission is not { } ended)
        {
            return ConductResult.Fail(transition.Refusal ?? "refused");
        }
        missions.Save(ended);
        Revoke(mission);
        BestEffort(MissionAuditEvents.MissionEnded, mission.Id, Text($"the release did not verify ({string.Join(", ", failures)}): back to the first world"));
        var opened = Open(run with { CurrentWorld = first }, first, MandateSource.Controller);
        // Like every answer where a check failed and the mission moved: not Ok, the new mission and its mandate, the failures that sent the problem back
        return opened.Ok ? new ConductResult(false, "the release did not verify: the problem goes back to the first world", failures, opened.Mission, opened.Mandate) : opened;
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
        var sentBack = new AuditLine(MissionAuditEvents.MissionSentBack, mission.Id, Text($"send-back {sent.SendBacks}"));
        if (sent.State == MissionState.Ended)
        {
            Settle(mission, sent, run with { State = ProblemState.Failed, Note = sent.EndReason }, sentBack, new AuditLine(MissionAuditEvents.ProblemFailed, run.ProblemId, sent.EndReason));
        }
        else
        {
            missions.Save(sent);
            BestEffort(sentBack);
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
        Settle(mission, ended, run with { State = ProblemState.Escalated, Note = reason },
            new AuditLine(MissionAuditEvents.MissionEnded, mission.Id, Text($"escalated: {reason}")), new AuditLine(MissionAuditEvents.ProblemEscalated, run.ProblemId, reason));
        return ConductResult.Success(ended);
    }

    private ConductResult End(ProblemRun run, Mission mission, MissionEvent ending, string reason)
    {
        var transition = MissionMachine.Apply(mission, ending, options.Loop, reason: reason);
        if (transition.Mission is not { } ended)
        {
            return ConductResult.Fail(transition.Refusal ?? "refused");
        }
        Settle(mission, ended, run with { State = ProblemState.Failed, Note = reason },
            new AuditLine(MissionAuditEvents.MissionEnded, mission.Id, Text($"{ended.End}: {reason}")), new AuditLine(MissionAuditEvents.ProblemFailed, run.ProblemId, reason));
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

    // The end of a mission and the new state of its problem, written once and always in this order: the mission, then the revoke of its mandate, then the problem (a mandate is never left in
    // force under a problem that says it is over), then the trail
    private void Settle(Mission before, Mission after, ProblemRun problem, params AuditLine[] trail)
    {
        missions.Save(after);
        Revoke(before);
        problems.Save(problem);
        foreach (var line in trail)
        {
            BestEffort(line);
        }
    }

    private void Revoke(Mission mission)
    {
        if (mission.MandateId is { } id && !mandates.IsRevoked(id))
        {
            mandates.Revoke(id);
            BestEffort(MissionAuditEvents.MandateRevoked, id, Text($"mission {mission.Id}"));
        }
    }

    // The frame of a mission: the repositories of the plan entry, and the forbidden paths of the settings **and** of the certificate (the floor of the authority), written once
    private MissionFrame FrameFor(ProblemRun run, string place) =>
        new(run.Entry.Repositories, [.. options.ForbiddenPaths.Union(run.Approval.Certificate.ForbiddenPaths, StringComparer.Ordinal)], place);

    // A new mission in a world, and the mandate for it: signed, recorded, counted, audited. The stop and the loop are checked here too, not only at a grant.
    private ConductResult Open(ProblemRun run, World world, MandateSource source)
    {
        if (stop.Engaged)
        {
            return Failed(run, "the global stop is engaged", [PreconditionFailure.KillSwitchEngaged]);
        }
        var entry = run.Entry;
        var loop = options.Loop.Check(mandates.CountForRun(run.ProblemId, run.Approval.Certificate.Id), time.GetUtcNow() - run.StartedAt);
        if (loop != LoopVerdict.Allowed)
        {
            return Failed(run, $"the loop reached its limit: {loop}", [PreconditionFailure.LoopLimitReached]);
        }
        if (mandates.CountForApproval(run.Approval.Certificate.Id) >= run.Approval.Certificate.MaxMandates)
        {
            return Failed(run, "the certificate of the approval backs no more mandates", [PreconditionFailure.ApprovalBacksNoMoreMandates]);
        }
        var frame = FrameFor(run, $"{world}:{entry.ApplicationId}");
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
        lock (locks.Of(problemId))
        {
            return work();
        }
    }

    // Granting a power is written first and refused when it cannot be written; withdrawing a power never waits for the trail
    private bool Required(string eventName, string subject, string? detail) => audit.Record(eventName, subject, detail);

    private void BestEffort(string eventName, string subject, string? detail) => _ = audit.Record(eventName, subject, detail);

    private void BestEffort(AuditLine line) => BestEffort(line.Event, line.Subject, line.Detail);

    // One line of the audit trail, kept to be written after the state is saved
    private readonly record struct AuditLine(string Event, string Subject, string? Detail);

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string ObjectiveOf(World world, PlanEntry entry) => world switch
    {
        World.Staging => entry.Objective,
        World.Git => $"The change for \"{entry.Objective}\" is merged into the integration branch with every pipeline green.",
        _ => $"The change for \"{entry.Objective}\" runs in production and is verified.",
    };
}
