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
    private readonly IMissionUnit unit = stores.Unit;
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
        // The plan of the lead can only stop the work earlier than the powers allow, never go further (ADR 0046)
        if (entry.Plan is { } plan && plan.Last < approved)
        {
            approved = plan.Last;
        }
        return EffectivePower.Of(approved, grants.MaxWorldFor(entry.ApplicationId));
    }

    // The highest world the powers reach without the plan of the lead, which is what the plan is checked against (the plan itself narrows HighestNow)
    private World? HighestWithoutPlan(PlanEntry entry, SignedApproval approval) => HighestNow(entry with { Plan = null }, approval);

    // The certificate must say at least what the entry asks: the repositories, the budget
    private static bool Covers(SignedApproval approval, PlanEntry entry) =>
        string.Equals(approval.Certificate.ApplicationId, entry.ApplicationId, StringComparison.Ordinal)
        && (approval.Certificate.Kind == CertificateKind.Problem ? string.Equals(approval.Certificate.Subject, entry.ProblemId, StringComparison.Ordinal) : string.Equals(approval.Certificate.Subject, entry.ProblemClass, StringComparison.Ordinal))
        && entry.Repositories.All(repository => approval.Certificate.Repositories.Contains(repository, StringComparer.Ordinal))
        && entry.Budget.MaxMinutes <= approval.Certificate.Budget.MaxMinutes && entry.Budget.MaxAttempts <= approval.Certificate.Budget.MaxAttempts && entry.Budget.MaxTurns <= approval.Certificate.Budget.MaxTurns;

    /// <summary>Starts a problem that is in the plan: the stop, the proved approval, the staging gate, the powers, then the first mandate in the first world.</summary>
    public async Task<ConductResult> StartAsync(PlanEntry entry, StagingDeclaration declaration, StagingRequirement requirement, StagingState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return await LockedAsync(entry.ProblemId, async () =>
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
            // A plan of the lead is checked before anything starts, against the powers as they are now: a plan that fails is refused, and nothing runs on a default that nobody chose
            if (entry.Plan is not null && LeadPlanChecks.Problems(entry.Plan, first, HighestWithoutPlan(entry, approval) ?? highest) is { Count: > 0 } refused)
            {
                return ConductResult.Fail($"the plan of the lead is refused: {string.Join("; ", refused)}");
            }
            if (!await RequiredAsync(MissionAuditEvents.ProblemStarted, entry.ProblemId, Text($"application {entry.ApplicationId}, validated by {entry.Validation.By}, up to {highest}, staging {(usesStaging ? "used" : "declared none")}"), cancellationToken))
            {
                return ConductResult.Fail("the audit trail cannot be written: no power is granted");
            }
            // The run is kept by Open once the first mandate exists: a start that fails leaves nothing in progress
            var run = new ProblemRun(entry.ProblemId, entry.ApplicationId, ProblemState.InProgress, time.GetUtcNow(), entry, highest, usesStaging, approval, first);
            if (first == World.Git)
            {
                // An application with no staging starts in the Git world: the frame of the merge is checked here too, since no crossing will do it (the diff, the CI and the executed files are checked at the merge itself)
                var frame = await GitFrameFailuresAsync(run, cancellationToken);
                if (frame.Count > 0)
                {
                    return ConductResult.Fail("the merge could deploy beyond the approval, or the agent could create tags or reach a write credential", frame);
                }
            }
            return await OpenAsync(run, first, byRule ? MandateSource.StandingRule : MandateSource.PlanValidation);
        }, cancellationToken);
    }

    /// <summary>The runner accepted the mandate and the worker started.</summary>
    public async Task<ConductResult> StartedAsync(string missionId, CancellationToken cancellationToken) => await OnMissionAsync(missionId, mission => Task.FromResult(Move(mission, MissionEvent.Started)), cancellationToken);

    /// <summary>The worker claims it is done: the dossier is kept as an untrusted claim and the mission waits for the controller.</summary>
    public async Task<ConductResult> HandOverAsync(string missionId, Dossier dossier, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dossier);
        if (!string.Equals(dossier.MissionId, missionId, StringComparison.Ordinal))
        {
            return ConductResult.Fail("the dossier is for another mission");
        }
        return await OnMissionAsync(missionId, async mission =>
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
                await BestEffortAsync(MissionAuditEvents.MissionHandedOver, missionId, Text($"complex: {dossier.Complex}"));
            }
            return result;
        }, cancellationToken);
    }

    /// <summary>Applies the controller's decision on a mission that handed over.</summary>
    public async Task<ConductResult> DecideAsync(string missionId, ControllerDecision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return await OnMissionAsync(missionId, async mission =>
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
            var decided = await RequiredAsync(MissionAuditEvents.ControllerDecided, missionId, Text($"{decision.Kind}: {string.Join(" | ", decision.Reasons)}"), cancellationToken);
            if (!decided && decision.Kind == ControllerDecisionKind.Grant)
            {
                return ConductResult.Fail("the audit trail cannot be written: no power is granted");
            }
            return decision.Kind switch
            {
                ControllerDecisionKind.Grant => await GrantAsync(run, mission, decision, cancellationToken),
                ControllerDecisionKind.SendBack => mission.World == World.Release ? await ReleaseJudgedFailedAsync(mission, run, cancellationToken) : await SendBackAsync(mission, run),
                ControllerDecisionKind.End => await EndAsync(run, mission, MissionEvent.EndedByController, decision.Reasons[0]),
                _ => await EscalateAsync(run, mission, decision.Reasons[0]),
            };
        }, cancellationToken);
    }

    private async Task<ConductResult> GrantAsync(ProblemRun run, Mission mission, ControllerDecision decision, CancellationToken cancellationToken)
    {
        var dossier = dossiers.Get(mission.Id);
        var complex = dossier?.Complex ?? false;
        if (mission.World == World.Release)
        {
            // The last world: the controller closes the problem on facts (FR-CTL-9), after the same checks
            var closing = await ClosingChecksAsync(run, mission, dossier, cancellationToken);
            return closing.Count > 0 ? await RouteFailuresAsync(mission, run, closing, cancellationToken) : await CloseAsync(run, mission, complex, null, "release verified", null);
        }
        // The power is read again here, at every crossing: a grant withdrawn since the start stops the problem (the grant of an application is the maintainer's to give and to take back)
        if (HighestNow(run.Entry, run.Approval) is not { } highest)
        {
            return await EndAsync(run, mission, MissionEvent.EndedByController, "the grant of the application was withdrawn");
        }
        var next = mission.World + 1;
        if (next > highest)
        {
            // The powers given stop here: the problem is done up to the world its powers allow, and says so (a plan validated up to the Git world ends there)
            var checks = await ClosingChecksAsync(run, mission, dossier, cancellationToken);
            return checks.Count > 0
                ? await RouteFailuresAsync(mission, run, checks, cancellationToken)
                : await CloseAsync(run, mission, complex, $"done up to the {highest} world, the highest that its powers allow", Text($"highest world reached: {highest}"), "done up to the highest world its powers allow");
        }
        var failures = await CheckFactsAsync(run, new FactsCheck(next, mission.Frame, mission.Id, dossier, HasPreviousDossier: dossier is not null, Commit: dossier?.Commit, Issuing: true, new FactsScope()), cancellationToken);
        if (!ControllerLimits.MayGrant(decision, next, highest, failures))
        {
            return failures.Count > 0 ? await RouteFailuresAsync(mission, run, failures, cancellationToken) : ConductResult.Fail("the controller may not grant a world that the plan does not allow");
        }
        var accepted = MissionMachine.Apply(mission, MissionEvent.Granted, options.Loop, complex: complex).Mission ?? mission;
        // The mandate of an accepted mission is withdrawn at once: the power never outlives the work, and the two writes go in together
        string? revoked = null;
        unit.Atomically(() =>
        {
            missions.Save(accepted);
            revoked = RevokeStore(mission);
        });
        await TellRevokedAsync(mission, revoked);
        await BestEffortAsync(MissionAuditEvents.MissionAccepted, mission.Id, Text($"next world {next}"));
        return await OpenAsync(run with { CurrentWorld = next }, next, MandateSource.Controller);
    }

    // The mission is accepted and the problem is closed: the mandate is revoked, the problem says how far it went
    private async Task<ConductResult> CloseAsync(ProblemRun run, Mission mission, bool complex, string? problemNote, string acceptedDetail, string? closedDetail)
    {
        var closed = MissionMachine.Apply(mission, MissionEvent.Granted, options.Loop, complex: complex).Mission ?? mission;
        await SettleAsync(mission, closed, run with { State = ProblemState.Closed, CurrentWorld = null, Note = problemNote },
            new AuditLine(MissionAuditEvents.MissionAccepted, mission.Id, acceptedDetail), new AuditLine(MissionAuditEvents.ProblemClosed, run.ProblemId, closedDetail));
        return ConductResult.Success(closed);
    }

    // The frame of the merge in the Git world, read now: the merge that may deploy, the tags, a write credential, the identity of the agent. Where no crossing makes these checks (an application with no staging
    // starts in the Git world, and a problem that goes back to it is in the same case), the conductor makes them before it issues the mandate
    private async Task<List<PreconditionFailure>> GitFrameFailuresAsync(ProblemRun run, CancellationToken cancellationToken)
    {
        var failures = await CheckFactsAsync(run, new FactsCheck(World.Git, FrameFor(run, "start"), null, null, HasPreviousDossier: true, Commit: null, Issuing: false, new FactsScope()), cancellationToken);
        return [.. failures.Where(failure => failure is PreconditionFailure.MergeMayDeployUnderGitMandate or PreconditionFailure.AgentCouldCreateTags or PreconditionFailure.AgentBranchCouldReachWriteCredentials or PreconditionFailure.AgentIdentityNotConfined)];
    }

    private async Task<ConductResult> SendBackAsync(Mission mission, ProblemRun run)
    {
        var transition = MissionMachine.Apply(mission, MissionEvent.SentBack, options.Loop);
        if (transition.Mission is not { } sent)
        {
            return ConductResult.Fail(transition.Refusal ?? "refused");
        }
        var sentBack = new AuditLine(MissionAuditEvents.MissionSentBack, mission.Id, Text($"send-back {sent.SendBacks}"));
        if (sent.State == MissionState.Ended)
        {
            await SettleAsync(mission, sent, run with { State = ProblemState.Failed, Note = sent.EndReason }, sentBack, new AuditLine(MissionAuditEvents.ProblemFailed, run.ProblemId, sent.EndReason));
        }
        else
        {
            missions.Save(sent);
            await BestEffortAsync(sentBack);
        }
        return ConductResult.Success(sent);
    }

    // The controller escalated: a decision for the maintainer. The mission ends, its mandate is revoked, and the problem waits as Escalated, told, never as a failure.
    private async Task<ConductResult> EscalateAsync(ProblemRun run, Mission mission, string reason, MissionEvent ending = MissionEvent.EndedByController)
    {
        var transition = MissionMachine.Apply(mission, ending, options.Loop, reason: $"escalated to the maintainer: {reason}");
        if (transition.Mission is not { } ended)
        {
            return ConductResult.Fail(transition.Refusal ?? "refused");
        }
        await SettleAsync(mission, ended, run with { State = ProblemState.Escalated, Note = reason },
            new AuditLine(MissionAuditEvents.MissionEnded, mission.Id, Text($"escalated: {reason}")), new AuditLine(MissionAuditEvents.ProblemEscalated, run.ProblemId, reason));
        return ConductResult.Success(ended);
    }

    private async Task<ConductResult> EndAsync(ProblemRun run, Mission mission, MissionEvent ending, string reason)
    {
        var transition = MissionMachine.Apply(mission, ending, options.Loop, reason: reason);
        if (transition.Mission is not { } ended)
        {
            return ConductResult.Fail(transition.Refusal ?? "refused");
        }
        await SettleAsync(mission, ended, run with { State = ProblemState.Failed, Note = reason },
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
    private async Task SettleAsync(Mission before, Mission after, ProblemRun problem, params AuditLine[] trail)
    {
        // The three writes go in together or not at all: a mission that is over, its mandate revoked and the problem that follows
        string? revoked = null;
        unit.Atomically(() =>
        {
            missions.Save(after);
            revoked = RevokeStore(before);
            problems.Save(problem);
        });
        await TellRevokedAsync(before, revoked);
        foreach (var line in trail)
        {
            await BestEffortAsync(line);
        }
    }

    // The write of a revoke: the identifier of the mandate that was revoked now, or null when there was none to revoke
    private string? RevokeStore(Mission mission)
    {
        if (mission.MandateId is not { } id || mandates.IsRevoked(id))
        {
            return null;
        }
        mandates.Revoke(id);
        return id;
    }

    private async Task TellRevokedAsync(Mission mission, string? revoked)
    {
        if (revoked is not null)
        {
            await BestEffortAsync(MissionAuditEvents.MandateRevoked, revoked, Text($"mission {mission.Id}"));
        }
    }

    // The frame of a mission: the repositories of the plan entry, and the forbidden paths of the settings **and** of the certificate (the floor of the authority), written once
    private MissionFrame FrameFor(ProblemRun run, string place) =>
        new(run.Entry.Repositories, [.. options.ForbiddenPaths.Union(run.Approval.Certificate.ForbiddenPaths, StringComparer.Ordinal)], place);

    // **Not cancellable**: it runs where the state has already moved (the mission before is accepted or ended and its mandate revoked), so a cancellation here would leave a problem with no mission to follow; the token is
    // read before the first write, never after.
    // A new mission in a world, and the mandate for it: signed, recorded, counted, audited. The stop and the loop are checked here too, not only at a grant.
    private async Task<ConductResult> OpenAsync(ProblemRun run, World world, MandateSource source)
    {
        if (stop.Engaged)
        {
            return await FailedAsync(run, "the global stop is engaged", [PreconditionFailure.KillSwitchEngaged]);
        }
        var entry = run.Entry;
        var loop = options.Loop.Check(mandates.CountForRun(run.ProblemId, run.Approval.Certificate.Id), time.GetUtcNow() - run.StartedAt);
        if (loop != LoopVerdict.Allowed)
        {
            return await FailedAsync(run, $"the loop reached its limit: {loop}", [PreconditionFailure.LoopLimitReached]);
        }
        if (mandates.CountForApproval(run.Approval.Certificate.Id) >= run.Approval.Certificate.MaxMandates)
        {
            return await FailedAsync(run, "the certificate of the approval backs no more mandates", [PreconditionFailure.ApprovalBacksNoMoreMandates]);
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
            return await FailedAsync(run, $"the mandate does not fall inside the approval: {covered}");
        }
        // The mandate is written to the audit trail before it exists: a power is never granted on an entry that cannot be written
        if (!await RequiredAsync(MissionAuditEvents.MandateIssued, mandate.Id, Text($"{world} for {mandate.RunnerId}, {source}, until {mandate.ExpiresAt:u}, acts {string.Join(",", mandate.Acts)}"), CancellationToken.None))
        {
            return await FailedAsync(run, "the audit trail cannot be written: no power is granted");
        }
        var signed = signer.Sign(mandate, run.Approval);
        var mandated = MissionMachine.Apply(mission, MissionEvent.MandateIssued, options.Loop, mandateId: mandate.Id).Mission ?? mission;
        // The mandate, the mission it is for and the problem that moves to this world are written together or not at all: no mandate without a mission, no mission without the problem that follows it
        unit.Atomically(() =>
        {
            mandates.Record(signed);
            missions.Save(mandated);
            problems.Save(run with { CurrentWorld = world });
        });
        return ConductResult.Success(mandated, signed);
    }

    // A mandate that cannot be issued ends the problem as failed, with the reason and an entry in the trail: nothing is left in progress with no mission and no way forward
    private async Task<ConductResult> FailedAsync(ProblemRun run, string reason, IReadOnlyList<PreconditionFailure>? failures = null)
    {
        problems.Save(run with { State = ProblemState.Failed, Note = reason });
        await BestEffortAsync(MissionAuditEvents.ProblemFailed, run.ProblemId, reason);
        return ConductResult.Fail(reason, failures);
    }

    // Every operation on a problem runs alone: two calls that overlap cannot both pass the same check of the state and issue two mandates
    private async Task<ConductResult> OnMissionAsync(string missionId, Func<Mission, Task<ConductResult>> work, CancellationToken cancellationToken)
    {
        if (missions.Get(missionId) is not { } first)
        {
            return ConductResult.Fail("unknown mission");
        }
        return await LockedAsync(first.ProblemId, async () => missions.Get(missionId) is { } mission ? await work(mission) : ConductResult.Fail("unknown mission"), cancellationToken);
    }

    // Every operation on a problem runs alone, and waits for its turn without holding a thread: the lock is one semaphore per problem
    private async Task<T> LockedAsync<T>(string problemId, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        var gate = locks.Of(problemId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await work();
        }
        finally
        {
            _ = gate.Release();
        }
    }

    // Granting a power is written first and refused when it cannot be written; withdrawing a power never waits for the trail
    private async Task<bool> RequiredAsync(string eventName, string subject, string? detail, CancellationToken cancellationToken) => await audit.RecordAsync(eventName, subject, detail, cancellationToken);

    private async Task BestEffortAsync(string eventName, string subject, string? detail) => _ = await audit.RecordAsync(eventName, subject, detail, CancellationToken.None);

    private async Task BestEffortAsync(AuditLine line) => await BestEffortAsync(line.Event, line.Subject, line.Detail);

    // One line of the audit trail, kept to be written after the state is saved
    private readonly record struct AuditLine(string Event, string Subject, string? Detail);

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    // Each world is told its own part of the problem, and only that: a controller or a worker that reads "merge it and verify the release" in the Git world
    // asks it for what the release world produces. A pull request is named by its number and repository (trusted values); another problem is quoted whole.
    private static string ObjectiveOf(World world, PlanEntry entry) => WithInstruction(world, entry, StatedFor(world, entry));

    // The instruction that the lead wrote for this world (ADR 0046) comes after what the control plane states itself, and is told for what it is
    private static string WithInstruction(World world, PlanEntry entry, string stated) =>
        entry.Plan?.InstructionFor(world) is { } instruction ? Text($"{stated} Instruction of the lead agent for this world: {instruction}") : stated;

    private static string StatedFor(World world, PlanEntry entry)
    {
        if (world == World.Staging)
        {
            return entry.Objective;
        }
        var change = entry.PullRequest is { } number && entry.Repositories.Count == 1 ? Text($"pull request #{number} of {entry.Repositories[0]}") : Text($"\"{entry.Objective}\"");
        return world == World.Git
            ? Text($"The change of {change} is merged into the integration branch with every pipeline green. Nothing is released at this stage.")
            : Text($"The change of {change} runs in production and is verified: its health check answers with a success.");
    }
}
