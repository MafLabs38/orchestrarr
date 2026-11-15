using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Core.Test.Missions;

public sealed class ProblemConductorTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly MandateSigner _signer = MandateSigner.Create();
    private readonly InMemoryMissionStore _missions = new();
    private readonly InMemoryMandateLedger _mandates = new();
    private readonly InMemoryProblemStore _problems = new();
    private readonly InMemoryDossierStore _dossiers = new();
    private readonly FakeFacts _facts = new();
    private readonly FakeStop _stop = new();
    private readonly TestAuthority _authority;
    private readonly FakeGrants _grants = new();
    private readonly FakeWithdrawals _withdrawals = new();
    private readonly FakeAudit _audit = new();
    private int _ids;

    public ProblemConductorTests() => _authority = new TestAuthority(_time);

    public void Dispose()
    {
        _signer.Dispose();
        _authority.Dispose();
    }

    private sealed class FakeFacts : IFactsSource
    {
        public bool CiGreen { get; set; } = true;

        public bool ArtifactProved { get; set; } = true;

        public bool ClaimMatches { get; set; } = true;

        public bool DiffTouchesForbidden { get; set; }

        public bool? WayBack { get; set; } = true;

        public bool? MergeMayDeploy { get; set; } = false;

        public bool? ExecutedFiles { get; set; } = false;

        public bool? TagsBlocked { get; set; } = true;

        public bool? BranchSafe { get; set; } = true;

        /// <summary>What the forge says the head is; null: the head is the commit that was asked about.</summary>
        public string? HeadCommit { get; set; }

        public ObservedFacts Facts(string problemId, World world, Dossier? dossier, string? commit) => new(true, !DiffTouchesForbidden, CiGreen, ArtifactProved, WayBack, ClaimMatches, MergeMayDeploy, ExecutedFiles, TagsBlocked, BranchSafe, HeadCommit ?? commit);
    }

    private sealed class FakeStop : IStopSignal
    {
        public bool Engaged { get; set; }
    }

    private sealed class FakeGrants : IGrantSource
    {
        public World? Max { get; set; } = World.Release;

        public World? MaxWorldFor(string applicationId) => Max;
    }

    private sealed class FakeWithdrawals : IApprovalWithdrawals
    {
        public HashSet<string> Withdrawn { get; } = [];

        public bool IsWithdrawn(string certificateId) => Withdrawn.Contains(certificateId);
    }

    private sealed class FakeAudit : IMissionAudit
    {
        public List<string> Events { get; } = [];

        public bool Writable { get; set; } = true;

        public bool Record(string eventName, string subject, string? detail)
        {
            lock (Events)
            {
                Events.Add($"{eventName} {subject} {detail}");
            }
            return Writable;
        }
    }

    private sealed class Runners : IRunnerDirectory
    {
        public string RunnerFor(World world, string applicationId) => $"runner-{world}".ToLowerInvariant();
    }

    private ProblemConductor Conductor(ConductorOptions? options = null) => Build(_authority, options, "id", locks: null);

    private ProblemConductor Build(IApprovalSource approvals, ConductorOptions? options, string idPrefix, ProblemLocks? locks) =>
        new(_signer, new MissionStores(_missions, _mandates, _problems, _dossiers), new PowerSources(approvals, _authority.Verifier, _withdrawals, _grants, _stop), _facts, new Runners(), _audit, _time, options ?? ConductorOptions.Default, () => $"{idPrefix}-{Interlocked.Increment(ref _ids)}", locks);

    private static PlanEntry Entry(World max = World.Release, string rule = "") => new("problem-1", "shop", "deprecated-dependency", "Replace the removed method", ["example/shop"], max, new MissionBudget(20, 3, 50),
        new Validation("alice", rule.Length == 0 ? null : rule), new DateTimeOffset(2026, 11, 13, 11, 0, 0, TimeSpan.Zero));

    private static Dossier DossierFor(string missionId, bool complex = false) => new(missionId, "done", "replaced the method and adapted the tests", ["sha256:aa"], [], complex, complex ? "a refactor" : null);

    private static ControllerDecision Grant() => new(ControllerDecisionKind.Grant, ["the evidence supports the claim"], null);

    private static ConductResult StartStaged(ProblemConductor conductor, PlanEntry? entry = null) =>
        conductor.Start(entry ?? Entry(), StagingDeclaration.DeclaredStaging, StagingRequirement.FullStack, StagingState.BaselineProven);

    private static ConductResult Through(ProblemConductor conductor, ConductResult step, bool complex = false)
    {
        var id = step.Mission!.Id;
        conductor.Started(id).Ok.Should().BeTrue();
        conductor.HandOver(id, DossierFor(id, complex)).Ok.Should().BeTrue();
        return conductor.Decide(id, Grant());
    }

    [Fact]
    public void A_problem_with_a_proven_staging_goes_through_the_three_worlds_each_with_a_mandate_for_its_runner_and_ends_closed()
    {
        var conductor = Conductor();
        var staging = StartStaged(conductor);
        staging.Mandate!.Mandate.World.Should().Be(World.Staging);
        staging.Mandate.Mandate.Source.Should().Be(MandateSource.PlanValidation);
        staging.Mandate.Mandate.RunnerId.Should().Be("runner-staging");

        var git = Through(conductor, staging);
        git.Mandate!.Mandate.World.Should().Be(World.Git);
        git.Mandate.Mandate.Source.Should().Be(MandateSource.Controller);
        git.Mandate.Mandate.Acts.Should().BeEquivalentTo([GatedAct.OpenPullRequest, GatedAct.MergePullRequest]);

        var release = Through(conductor, git);
        release.Mandate!.Mandate.World.Should().Be(World.Release);
        release.Mandate.Mandate.Acts.Should().BeEquivalentTo([GatedAct.Deploy, GatedAct.Rollback]);

        var closed = Through(conductor, release);

        closed.Ok.Should().BeTrue();
        closed.Mission!.State.Should().Be(MissionState.Accepted);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Closed);
        _missions.ForProblem("problem-1").Should().HaveCount(3);
    }

    [Fact]
    public void Each_mandate_is_signed_for_its_runner_and_its_world_and_no_other_runner_accepts_it()
    {
        var conductor = Conductor();
        var staging = StartStaged(conductor);
        var git = Through(conductor, staging);
        var state = new RunnerState();

        using var gitRunner = new MandateVerifier(_signer.PublicKey(), "runner-git", World.Git, state, _time, _authority.Verifier);
        using var stagingRunner = new MandateVerifier(_signer.PublicKey(), "runner-staging", World.Staging, state, _time, _authority.Verifier);

        stagingRunner.Accept(git.Mandate!).Should().Be(MandateRefusal.WrongRunner);
        gitRunner.Accept(git.Mandate!).Should().Be(MandateRefusal.None);
    }

    [Fact]
    public void An_application_declared_as_having_no_staging_starts_in_the_git_world()
    {
        var conductor = Conductor();

        var result = conductor.Start(Entry(), StagingDeclaration.DeclaredNoStaging, StagingRequirement.BlankMachine, StagingState.NotDefined);

        result.Mandate!.Mandate.World.Should().Be(World.Git);
    }

    [Theory]
    [InlineData(StagingDeclaration.Undeclared, StagingState.BaselineProven)]
    [InlineData(StagingDeclaration.DeclaredStaging, StagingState.Broken)]
    [InlineData(StagingDeclaration.DeclaredStaging, StagingState.Stale)]
    [InlineData(StagingDeclaration.DeclaredStaging, StagingState.Defined)]
    public void A_staging_that_is_not_declared_or_not_proven_refuses_the_first_mandate_and_leaves_nothing_started(StagingDeclaration declaration, StagingState state)
    {
        var conductor = Conductor();

        var result = conductor.Start(Entry(), declaration, StagingRequirement.FullStack, state);

        result.Ok.Should().BeFalse();
        result.Failures.Should().Contain(PreconditionFailure.StagingNotUsable);
        _missions.ForProblem("problem-1").Should().BeEmpty();
        _problems.Get("problem-1").Should().BeNull();
    }

    [Fact]
    public void A_problem_is_not_started_twice_and_a_grant_that_is_not_given_is_no_power()
    {
        var conductor = Conductor();
        StartStaged(conductor).Ok.Should().BeTrue();

        StartStaged(conductor).Refusal.Should().Contain("already");
        _grants.Max = null;
        var noGrant = conductor.Start(Entry() with { ProblemId = "problem-2" }, StagingDeclaration.DeclaredStaging, StagingRequirement.BlankMachine, StagingState.NotDefined);

        noGrant.Ok.Should().BeFalse();
        noGrant.Refusal.Should().Contain("grant is off");
        _problems.Get("problem-2").Should().BeNull();
    }

    [Fact]
    public void An_approval_that_is_not_proved_issues_no_mandate_and_a_validation_by_name_alone_is_not_one()
    {
        _authority.Issue = false;

        var result = StartStaged(Conductor());

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("approval").And.Contain("not proved");
        _missions.ForProblem("problem-1").Should().BeEmpty();
    }

    [Fact]
    public void The_global_stop_refuses_a_first_mandate_and_a_next_one_and_ends_the_work_instead_of_issuing_it()
    {
        var conductor = Conductor();
        _stop.Engaged = true;
        StartStaged(conductor).Refusal.Should().Contain("global stop");
        _stop.Engaged = false;
        var staging = StartStaged(conductor);
        var id = staging.Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));
        _stop.Engaged = true;

        var result = conductor.Decide(id, Grant());

        result.Ok.Should().BeFalse();
        result.Failures.Should().Contain(PreconditionFailure.KillSwitchEngaged);
        _missions.ForProblem("problem-1").Should().ContainSingle("no mandate was issued under the stop");
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
    }

    [Fact]
    public void A_problem_validated_up_to_the_git_world_is_closed_there_and_never_given_a_release_mandate()
    {
        var conductor = Conductor();
        var staging = StartStaged(conductor, Entry(max: World.Git));
        var git = Through(conductor, staging);
        var id = git.Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));

        var closed = conductor.Decide(id, Grant());

        closed.Ok.Should().BeTrue();
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Closed);
        _problems.Get("problem-1")!.Note.Should().Contain("highest that its powers allow");
        _missions.ForProblem("problem-1").Should().HaveCount(2, "no mission was made for the release world");
    }

    [Fact]
    public void A_grant_over_a_failed_check_of_the_code_is_never_a_grant_the_mission_is_sent_back_with_the_failure()
    {
        var conductor = Conductor();
        var staging = StartStaged(conductor);
        var id = staging.Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));
        _facts.DiffTouchesForbidden = true;

        var result = conductor.Decide(id, Grant());

        result.Ok.Should().BeFalse();
        result.Failures.Should().Contain(PreconditionFailure.DiffTouchesForbiddenPath);
        result.Mission!.State.Should().Be(MissionState.Running);
        result.Mission.SendBacks.Should().Be(1);
        _missions.ForProblem("problem-1").Should().ContainSingle("no mandate for the next world was issued");
    }

    [Fact]
    public void A_claim_that_the_facts_do_not_support_is_sent_back_before_any_mandate_is_issued()
    {
        var conductor = Conductor();
        var staging = StartStaged(conductor);
        var id = staging.Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));
        _facts.ClaimMatches = false;

        var result = conductor.Decide(id, Grant());

        result.Failures.Should().Contain(PreconditionFailure.ClaimDoesNotMatchFacts);
    }

    [Fact]
    public void A_release_needs_the_green_pipeline_and_the_artifact_that_was_proved_in_staging()
    {
        var conductor = Conductor();
        var git = Through(conductor, StartStaged(conductor));
        var release = Through(conductor, git);
        var id = release.Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));
        _facts.ArtifactProved = false;

        var result = conductor.Decide(id, Grant());

        result.Failures.Should().Contain(PreconditionFailure.ArtifactNotProvedInStaging);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.InProgress);
    }

    [Fact]
    public void A_release_of_an_application_with_no_staging_needs_only_the_pipelines_artifact()
    {
        var conductor = Conductor();
        var start = conductor.Start(Entry(), StagingDeclaration.DeclaredNoStaging, StagingRequirement.BlankMachine, StagingState.NotDefined);
        var release = Through(conductor, start);
        _facts.ArtifactProved = false;

        var closed = Through(conductor, release);

        closed.Ok.Should().BeTrue();
    }

    [Fact]
    public void A_send_back_from_the_controller_continues_in_the_same_world_and_too_many_end_the_problem()
    {
        var conductor = Conductor(ConductorOptions.Default with { Loop = LoopPolicy.Default with { MaxSendBacksPerMission = 1 } });
        var id = StartStaged(conductor).Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));
        var sendBack = new ControllerDecision(ControllerDecisionKind.SendBack, ["incomplete"], "run the integration tests too");

        var first = conductor.Decide(id, sendBack);
        first.Mission!.State.Should().Be(MissionState.Running);
        conductor.HandOver(id, DossierFor(id));
        var second = conductor.Decide(id, sendBack);

        second.Mission!.State.Should().Be(MissionState.Ended);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
    }

    [Theory]
    [InlineData(ControllerDecisionKind.End, "the frame was not kept", ProblemState.Failed)]
    [InlineData(ControllerDecisionKind.Escalate, "no fixed version exists", ProblemState.Escalated)]
    public void The_controller_can_end_or_escalate_the_mandate_is_revoked_and_an_escalation_waits_for_the_maintainer_instead_of_failing(ControllerDecisionKind kind, string reason, ProblemState expected)
    {
        var conductor = Conductor();
        var staging = StartStaged(conductor);
        var id = staging.Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));

        var result = conductor.Decide(id, new ControllerDecision(kind, [reason], null));

        result.Mission!.State.Should().Be(MissionState.Ended);
        _mandates.IsRevoked(staging.Mandate!.Mandate.Id).Should().BeTrue();
        _problems.Get("problem-1")!.State.Should().Be(expected);
        _problems.Get("problem-1")!.Note.Should().Contain(reason);
    }

    [Fact]
    public void A_decision_without_reasons_or_on_a_mission_that_did_not_hand_over_is_refused()
    {
        var conductor = Conductor();
        var id = StartStaged(conductor).Mission!.Id;

        conductor.Decide(id, Grant()).Refusal.Should().Contain("handed over");
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));
        conductor.Decide(id, new ControllerDecision(ControllerDecisionKind.Grant, [], null)).Refusal.Should().Contain("reasons");
    }

    [Fact]
    public void A_dossier_for_another_mission_is_refused_and_a_complex_one_is_marked_on_the_accepted_mission()
    {
        var conductor = Conductor();
        var staging = StartStaged(conductor);
        var id = staging.Mission!.Id;
        conductor.Started(id);

        conductor.HandOver(id, DossierFor("another")).Ok.Should().BeFalse();
        conductor.HandOver(id, DossierFor(id, complex: true));
        conductor.Decide(id, Grant());

        _missions.Get(id)!.End.Should().Be(MissionEnd.DoneComplex);
    }

    [Fact]
    public void The_global_stop_revokes_every_mandate_ends_the_running_mission_and_fails_the_problem()
    {
        var conductor = Conductor();
        var staging = StartStaged(conductor);
        var git = Through(conductor, staging);
        conductor.Started(git.Mission!.Id);

        var stopped = conductor.Stop("problem-1", "the global stop");

        stopped.Ok.Should().BeTrue();
        _missions.Get(git.Mission.Id)!.End.Should().Be(MissionEnd.Stopped);
        _mandates.IsRevoked(git.Mandate!.Mandate.Id).Should().BeTrue();
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
    }

    [Fact]
    public void The_loop_of_a_problem_stops_when_it_has_had_its_mandates_or_its_time()
    {
        var conductor = Conductor(ConductorOptions.Default with { Loop = LoopPolicy.Default with { MaxMandatesPerProblem = 2 } });
        var staging = StartStaged(conductor);
        var git = Through(conductor, staging);
        var id = git.Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));

        var third = conductor.Decide(id, Grant());

        third.Ok.Should().BeFalse();
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
        _problems.Get("problem-1")!.Note.Should().Contain("limit");
    }

    [Fact]
    public void A_mission_that_uses_up_its_budget_fails_the_problem_cleanly()
    {
        var conductor = Conductor();
        var id = StartStaged(conductor).Mission!.Id;
        conductor.Started(id);

        var result = conductor.BudgetExhausted(id);

        result.Mission!.End.Should().Be(MissionEnd.Failed);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
    }

    [Fact]
    public void A_standing_rule_issues_the_first_mandate_as_a_standing_rule_and_its_ceiling_is_the_certificates()
    {
        _authority.Kind = CertificateKind.Rule;
        _authority.Adjust = request => request with { MaxWorld = World.Git };
        var conductor = Conductor();

        var result = StartStaged(conductor, Entry(rule: "rule-1"));

        result.Mandate!.Mandate.Source.Should().Be(MandateSource.StandingRule);
        _problems.Get("problem-1")!.HighestAllowed.Should().Be(World.Git, "the rule's certificate stops at the Git world");
    }

    [Fact]
    public void A_grant_withdrawn_while_the_problem_is_in_flight_stops_it_at_the_next_crossing_and_a_lowered_grant_closes_it_at_its_new_ceiling()
    {
        var withdrawn = Conductor();
        var staging = StartStaged(withdrawn);
        var id = staging.Mission!.Id;
        withdrawn.Started(id);
        withdrawn.HandOver(id, DossierFor(id));
        _grants.Max = null;

        var stopped = withdrawn.Decide(id, Grant());

        stopped.Mission!.End.Should().Be(MissionEnd.Refused);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
        _problems.Get("problem-1")!.Note.Should().Contain("withdrawn");
        _missions.ForProblem("problem-1").Should().ContainSingle("no next mandate was issued");

        _grants.Max = World.Release;
        var other = Entry() with { ProblemId = "problem-2" };
        var second = StartStaged(withdrawn, other);
        var secondId = second.Mission!.Id;
        withdrawn.Started(secondId);
        withdrawn.HandOver(secondId, DossierFor(secondId));
        _grants.Max = World.Staging;

        var closed = withdrawn.Decide(secondId, Grant());

        closed.Ok.Should().BeTrue();
        _problems.Get("problem-2")!.State.Should().Be(ProblemState.Closed);
        _problems.Get("problem-2")!.Note.Should().Contain("Staging");
    }

    [Fact]
    public void The_sweep_stops_a_problem_whose_grant_was_withdrawn_even_when_nothing_else_happens()
    {
        var conductor = Conductor();
        var id = StartStaged(conductor).Mission!.Id;
        conductor.Started(id);
        _grants.Max = null;

        conductor.Sweep().Should().Be(1);

        _problems.Get("problem-1")!.Note.Should().Contain("grant");
    }

    [Fact]
    public void A_certificate_that_says_less_than_the_plan_entry_asks_issues_no_mandate_and_a_lower_ceiling_in_the_certificate_wins_over_the_entry()
    {
        _authority.Adjust = request => request with { Repositories = ["example/other"] };
        StartStaged(Conductor()).Refusal.Should().Contain("does not cover");

        _authority.Adjust = request => request with { Budget = new MissionBudget(1, 1, 1) };
        StartStaged(Conductor()).Refusal.Should().Contain("does not cover");

        _authority.Adjust = request => request with { MaxWorld = World.Git };
        var result = StartStaged(Conductor(), Entry(max: World.Release));

        result.Ok.Should().BeTrue();
        _problems.Get("problem-1")!.HighestAllowed.Should().Be(World.Git);
    }

    [Fact]
    public void Every_mandate_carries_the_certificate_it_falls_inside_and_never_outlives_it()
    {
        _authority.Lifetime = TimeSpan.FromMinutes(10);
        var conductor = Conductor();

        var staging = StartStaged(conductor);

        staging.Mandate!.Approval.Should().NotBeNull();
        staging.Mandate.Mandate.ApprovalId.Should().Be(staging.Mandate.Approval.Certificate.Id);
        staging.Mandate.Mandate.ExpiresAt.Should().Be(staging.Mandate.Approval.Certificate.ExpiresAt, "the certificate expires before the default lifetime of a mandate");
        _authority.Verifier.Check(staging.Mandate.Approval, staging.Mandate.Mandate).Should().Be(ApprovalRefusal.None);
    }

    [Fact]
    public void A_merge_that_may_deploy_is_refused_under_an_approval_that_stops_before_the_release_world_and_unknown_counts_as_deploying()
    {
        _authority.Adjust = request => request with { MaxWorld = World.Git };
        var conductor = Conductor();
        var staging = StartStaged(conductor, Entry(max: World.Git));
        var id = staging.Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));
        _facts.MergeMayDeploy = true;

        var result = conductor.Decide(id, Grant());

        result.Failures.Should().Contain(PreconditionFailure.MergeMayDeployBeyondApproval);
        _facts.MergeMayDeploy = null;
        conductor.HandOver(id, DossierFor(id));
        conductor.Decide(id, Grant()).Failures.Should().Contain(PreconditionFailure.MergeMayDeployBeyondApproval);
        _facts.MergeMayDeploy = false;
        conductor.HandOver(id, DossierFor(id));
        conductor.Decide(id, Grant()).Mandate!.Mandate.World.Should().Be(World.Git);
    }

    [Fact]
    public void A_merge_that_may_deploy_with_changes_to_what_the_pipeline_executes_is_escalated_to_the_maintainer_not_sent_back()
    {
        var conductor = Conductor();
        var staging = StartStaged(conductor);
        var id = staging.Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));
        _facts.MergeMayDeploy = true;
        _facts.ExecutedFiles = true;

        var result = conductor.Decide(id, Grant());

        result.Failures.Should().Contain(PreconditionFailure.ExecutedFilesTouchedWhereMergeDeploys);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Escalated);
        _missions.ForProblem("problem-1").Should().ContainSingle("no mandate for the Git world was issued");
    }

    [Fact]
    public void An_escalated_problem_is_not_started_again_by_accident_and_the_audit_trail_says_so()
    {
        var conductor = Conductor();
        var id = StartStaged(conductor).Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));
        conductor.Decide(id, new ControllerDecision(ControllerDecisionKind.Escalate, ["no fixed version exists"], null));

        StartStaged(conductor).Refusal.Should().Contain("already");
        _audit.Events.Should().Contain(entry => entry.StartsWith("problem.escalated problem-1", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_mandate_decision_send_back_and_end_is_written_to_the_audit_trail()
    {
        var conductor = Conductor();
        var staging = StartStaged(conductor);
        var id = staging.Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));
        _facts.DiffTouchesForbidden = true;
        conductor.Decide(id, Grant());
        _facts.DiffTouchesForbidden = false;
        conductor.HandOver(id, DossierFor(id));
        conductor.Decide(id, new ControllerDecision(ControllerDecisionKind.End, ["the frame was not kept"], null));

        _audit.Events.Should().Contain(entry => entry.StartsWith("problem.started problem-1", StringComparison.Ordinal))
            .And.Contain(entry => entry.StartsWith("mandate.issued", StringComparison.Ordinal) && entry.Contains("Staging for runner-staging", StringComparison.Ordinal))
            .And.Contain(entry => entry.StartsWith("controller.decided", StringComparison.Ordinal) && entry.Contains("the evidence supports the claim", StringComparison.Ordinal))
            .And.Contain(entry => entry.StartsWith("mission.sent-back", StringComparison.Ordinal))
            .And.Contain(entry => entry.StartsWith("mandate.revoked", StringComparison.Ordinal))
            .And.Contain(entry => entry.StartsWith("mission.ended", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_calls_that_overlap_on_the_same_problem_cannot_both_start_it()
    {
        var conductor = Conductor();
        var results = new ConductResult[8];

        Parallel.For(0, results.Length, index => results[index] = StartStaged(conductor));

        results.Count(result => result.Ok).Should().Be(1);
        _missions.ForProblem("problem-1").Should().ContainSingle();
        _mandates.CountForProblem("problem-1").Should().Be(1);
    }

    [Fact]
    public void The_sweep_ends_a_mission_whose_mandate_expired_without_a_runner_and_the_problem_does_not_stay_in_progress_for_ever()
    {
        var conductor = Conductor();
        var id = StartStaged(conductor).Mission!.Id;
        conductor.Started(id);
        _time.Advance(TimeSpan.FromMinutes(61));

        var ended = conductor.Sweep();

        ended.Should().Be(1);
        _missions.Get(id)!.End.Should().Be(MissionEnd.Stopped);
        _missions.Get(id)!.EndReason.Should().Contain("expired");
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
        _mandates.IsRevoked(_missions.Get(id)!.MandateId!).Should().BeTrue();
    }

    [Fact]
    public void The_sweep_stops_every_problem_in_progress_when_the_global_stop_is_engaged_and_after_the_time_bound()
    {
        var conductor = Conductor();
        StartStaged(conductor);
        conductor.Sweep().Should().Be(0);
        _stop.Engaged = true;

        conductor.Sweep().Should().Be(1);
        _problems.Get("problem-1")!.Note.Should().Contain("global stop");

        _stop.Engaged = false;
        var other = Conductor();
        other.Start(Entry() with { ProblemId = "problem-9" }, StagingDeclaration.DeclaredStaging, StagingRequirement.BlankMachine, StagingState.NotDefined).Ok.Should().BeTrue();
        _time.Advance(TimeSpan.FromHours(7));
        other.Sweep().Should().Be(1);
        _problems.Get("problem-9")!.Note.Should().Contain("time limit");
    }

    [Fact]
    public void A_mission_waiting_for_the_controller_is_not_expired_by_the_sweep()
    {
        var conductor = Conductor();
        var id = StartStaged(conductor).Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));
        _time.Advance(TimeSpan.FromMinutes(90));

        conductor.Sweep().Should().Be(0);
        _missions.Get(id)!.State.Should().Be(MissionState.HandingOver);
    }

    [Fact]
    public void A_release_without_a_way_back_known_is_sent_back_a_deployment_needs_one_before_it_starts()
    {
        var conductor = Conductor();
        var git = Through(conductor, StartStaged(conductor));
        var release = Through(conductor, git);
        var id = release.Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));
        _facts.WayBack = null;

        var result = conductor.Decide(id, Grant());

        result.Failures.Should().Contain(PreconditionFailure.NoWayBackKnown);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.InProgress);
    }

    [Fact]
    public void A_power_is_never_granted_on_an_entry_that_the_audit_trail_cannot_write_and_a_power_is_always_withdrawn()
    {
        var conductor = Conductor();
        _audit.Writable = false;
        StartStaged(conductor).Refusal.Should().Contain("audit trail cannot be written");
        _missions.ForProblem("problem-1").Should().BeEmpty();

        _audit.Writable = true;
        var staging = StartStaged(conductor);
        var id = staging.Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));
        _audit.Writable = false;

        conductor.Decide(id, Grant()).Refusal.Should().Contain("audit trail cannot be written");
        _missions.ForProblem("problem-1").Should().ContainSingle("no next mandate was issued");

        conductor.Stop("problem-1", "the global stop").Ok.Should().BeTrue("withdrawing a power never waits for the trail");
        _mandates.IsRevoked(staging.Mandate!.Mandate.Id).Should().BeTrue();
    }

    [Fact]
    public void Two_conductors_on_the_same_stores_that_share_the_locks_do_not_overlap_on_a_problem()
    {
        var locks = new ProblemLocks();
        var one = Build(_authority, null, "a", locks);
        var two = Build(_authority, null, "b", locks);
        var results = new ConductResult[8];

        Parallel.For(0, results.Length, index => results[index] = StartStaged(index % 2 == 0 ? one : two));

        results.Count(result => result.Ok).Should().Be(1);
        _mandates.CountForProblem("problem-1").Should().Be(1);
    }

    [Fact]
    public void A_start_that_cannot_issue_its_mandate_leaves_nothing_in_progress_and_can_be_tried_again()
    {
        var conductor = Conductor();
        _audit.Writable = false;
        StartStaged(conductor).Ok.Should().BeFalse();
        _problems.Get("problem-1").Should().BeNull("nothing was kept");

        _audit.Writable = true;
        StartStaged(conductor).Ok.Should().BeTrue("the retry is not refused as already in progress");
    }

    [Fact]
    public void A_certificate_for_another_application_or_another_class_issues_no_mandate()
    {
        _authority.Adjust = request => request with { ApplicationId = "other-app" };
        StartStaged(Conductor()).Refusal.Should().Contain("does not cover");

        _authority.Adjust = null;
        _authority.Kind = CertificateKind.Rule;
        _authority.Adjust = request => request with { Subject = "secret-exposed" };
        StartStaged(Conductor(), Entry(World.Git)).Refusal.Should().Contain("does not cover");
    }

    [Fact]
    public void The_mandates_carry_the_forbidden_paths_of_the_certificate_and_the_application_and_class_of_the_problem()
    {
        _authority.Adjust = request => request with { ForbiddenPaths = ["infra/prod/**"] };
        var conductor = Conductor();

        var staging = StartStaged(conductor);

        staging.Mandate!.Mandate.Frame.ForbiddenPaths.Should().Contain("infra/prod/**").And.Contain(".github/**");
        staging.Mandate.Mandate.ApplicationId.Should().Be("shop");
        staging.Mandate.Mandate.ProblemClass.Should().Be("deprecated-dependency");
    }

    [Fact]
    public void A_stop_that_moves_the_problem_to_failed_is_a_stop_that_worked_even_with_no_mission_running()
    {
        var conductor = Conductor();
        var id = StartStaged(conductor).Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));
        conductor.Decide(id, new ControllerDecision(ControllerDecisionKind.SendBack, ["incomplete"], "more tests"));
        conductor.HandOver(id, DossierFor(id));
        var ended = conductor.Decide(id, new ControllerDecision(ControllerDecisionKind.End, ["frame broken"], null));
        ended.Ok.Should().BeTrue();

        conductor.Stop("problem-1", "x").Ok.Should().BeFalse("the problem is no longer in progress");
    }

    private static ConductResult StartUnstaged(ProblemConductor conductor, PlanEntry? entry = null) =>
        conductor.Start(entry ?? Entry(), StagingDeclaration.DeclaredNoStaging, StagingRequirement.BlankMachine, StagingState.NotDefined);

    [Fact]
    public void The_mandate_of_an_accepted_mission_is_revoked_at_every_crossing_and_when_the_problem_closes()
    {
        var conductor = Conductor();
        var staging = StartStaged(conductor);
        var git = Through(conductor, staging);
        _mandates.IsRevoked(staging.Mandate!.Mandate.Id).Should().BeTrue("the power never outlives the work");
        var release = Through(conductor, git);
        _mandates.IsRevoked(git.Mandate!.Mandate.Id).Should().BeTrue();

        Through(conductor, release).Ok.Should().BeTrue();

        _mandates.IsRevoked(release.Mandate!.Mandate.Id).Should().BeTrue("a closed problem leaves no mandate in force");
    }

    [Fact]
    public void A_problem_that_stops_at_the_git_world_revokes_that_mandate_when_it_closes_and_a_send_back_that_ends_the_mission_revokes_it_too()
    {
        var conductor = Conductor();
        var git = StartUnstaged(conductor, Entry(World.Git) with { ProblemId = "problem-1" });
        git.Mandate!.Mandate.World.Should().Be(World.Git);
        Through(conductor, git).Ok.Should().BeTrue();
        _mandates.IsRevoked(git.Mandate.Mandate.Id).Should().BeTrue();

        var looping = StartStaged(conductor, Entry() with { ProblemId = "problem-2" });
        var id = looping.Mission!.Id;
        for (var round = 0; round < 4 && _missions.Get(id)!.State != MissionState.Ended; round++)
        {
            conductor.Started(id);
            conductor.HandOver(id, DossierFor(id));
            conductor.Decide(id, new ControllerDecision(ControllerDecisionKind.SendBack, ["not yet"], "more tests"));
        }

        _missions.Get(id)!.State.Should().Be(MissionState.Ended);
        _mandates.IsRevoked(looping.Mandate!.Mandate.Id).Should().BeTrue();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(null, true)]
    [InlineData(false, false)]
    public void A_start_in_the_git_world_checks_the_frame_of_the_merge_because_no_crossing_will(bool? mayDeploy, bool tagsBlocked)
    {
        _facts.MergeMayDeploy = mayDeploy;
        _facts.TagsBlocked = tagsBlocked;

        var result = StartUnstaged(Conductor(), Entry(World.Git));

        result.Ok.Should().BeFalse();
        result.Failures.Should().Contain(mayDeploy == false ? PreconditionFailure.AgentCouldCreateTags : PreconditionFailure.MergeMayDeployBeyondApproval);
        _missions.ForProblem("problem-1").Should().BeEmpty();
        _problems.Get("problem-1").Should().BeNull();
    }

    [Fact]
    public void A_start_in_the_git_world_is_allowed_when_the_merge_does_not_deploy_and_tags_are_blocked_or_when_the_approval_reaches_the_release_world()
    {
        _facts.MergeMayDeploy = false;
        StartUnstaged(Conductor(), Entry(World.Git)).Ok.Should().BeTrue();

        _facts.MergeMayDeploy = true;
        StartUnstaged(Conductor(), Entry() with { ProblemId = "problem-2" }).Ok.Should().BeTrue("the approval reaches the release world, where the merge that deploys belongs");
    }

    [Fact]
    public void The_merge_ceiling_is_the_power_in_effect_now_not_the_certificate_alone()
    {
        // The certificate reaches the release world, the grant of the application was lowered to the Git world, and the merge may deploy
        _grants.Max = World.Git;
        _facts.MergeMayDeploy = true;
        var conductor = Conductor();
        var staging = StartStaged(conductor);
        var id = staging.Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));

        var decided = conductor.Decide(id, Grant());

        decided.Ok.Should().BeFalse();
        decided.Failures.Should().Contain(PreconditionFailure.MergeMayDeployBeyondApproval);
        _missions.ForProblem("problem-1").Should().ContainSingle("no Git mandate was issued");
    }

    [Fact]
    public void The_act_gateway_allows_a_merge_only_when_the_facts_of_this_moment_on_this_commit_hold_and_writes_it_down()
    {
        var conductor = Conductor();
        var git = Through(conductor, StartStaged(conductor));
        var mission = git.Mission!.Id;

        var allowed = conductor.AuthoriseAct(mission, GatedAct.MergePullRequest, "abc123");

        allowed.Allowed.Should().BeTrue();
        _audit.Events.Should().Contain(entry => entry.StartsWith("act.authorised") && entry.Contains("MergePullRequest on abc123"));

        _facts.CiGreen = false;
        var refused = conductor.AuthoriseAct(mission, GatedAct.MergePullRequest, "def456");

        refused.Allowed.Should().BeFalse();
        refused.Failures.Should().Contain(PreconditionFailure.CiNotGreenOnExactCommit);
        _audit.Events.Should().Contain(entry => entry.StartsWith("act.refused") && entry.Contains("def456"));
    }

    [Fact]
    public void The_act_gateway_refuses_a_diff_outside_the_frame_and_escalates_a_change_to_executed_files_where_the_merge_deploys()
    {
        var conductor = Conductor();
        var git = Through(conductor, StartStaged(conductor));
        var mission = git.Mission!.Id;

        _facts.DiffTouchesForbidden = true;
        conductor.AuthoriseAct(mission, GatedAct.MergePullRequest, "abc").Failures.Should().Contain(PreconditionFailure.DiffTouchesForbiddenPath);
        _facts.DiffTouchesForbidden = false;
        _facts.MergeMayDeploy = true;
        _facts.ExecutedFiles = true;

        var executed = conductor.AuthoriseAct(mission, GatedAct.MergePullRequest, "abc");

        executed.Allowed.Should().BeFalse();
        executed.Failures.Should().Contain(PreconditionFailure.ExecutedFilesTouchedWhereMergeDeploys);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Escalated);
        _mandates.IsRevoked(git.Mandate!.Mandate.Id).Should().BeTrue("the mission ended, a person decides");
    }

    [Fact]
    public void The_act_gateway_refuses_an_act_the_mandate_does_not_list_a_revoked_or_expired_mandate_a_stop_and_a_grant_lowered_below_the_world()
    {
        var conductor = Conductor();
        var git = Through(conductor, StartStaged(conductor));
        var mission = git.Mission!.Id;

        conductor.AuthoriseAct(mission, GatedAct.Deploy, "abc").Refusal.Should().Contain("does not list");
        conductor.AuthoriseAct("unknown", GatedAct.MergePullRequest, "abc").Refusal.Should().Contain("unknown");
        _grants.Max = World.Staging;
        conductor.AuthoriseAct(mission, GatedAct.MergePullRequest, "abc").Refusal.Should().Contain("no longer reach");
        _grants.Max = World.Release;
        _stop.Engaged = true;
        conductor.AuthoriseAct(mission, GatedAct.MergePullRequest, "abc").Refusal.Should().Contain("global stop");
        _stop.Engaged = false;
        _time.Advance(TimeSpan.FromMinutes(61));
        conductor.AuthoriseAct(mission, GatedAct.MergePullRequest, "abc").Refusal.Should().Contain("expired");
        _mandates.Revoke(git.Mandate!.Mandate.Id);
        conductor.AuthoriseAct(mission, GatedAct.MergePullRequest, "abc").Refusal.Should().Contain("no mandate in force");
    }

    [Fact]
    public void An_act_that_does_not_change_what_runs_needs_only_the_mandate_and_a_failing_trail_refuses_the_power()
    {
        var conductor = Conductor();
        var git = Through(conductor, StartStaged(conductor));
        _facts.CiGreen = false;

        conductor.AuthoriseAct(git.Mission!.Id, GatedAct.OpenPullRequest, null).Allowed.Should().BeTrue("opening a pull request changes nothing that runs");

        _audit.Writable = false;
        _facts.CiGreen = true;
        conductor.AuthoriseAct(git.Mission.Id, GatedAct.MergePullRequest, "abc").Refusal.Should().Contain("audit trail");
    }

    [Fact]
    public void An_approval_is_for_one_run_a_stopped_problem_is_not_started_again_on_it_but_a_new_approval_starts_it()
    {
        var conductor = Conductor();
        StartStaged(conductor).Ok.Should().BeTrue();
        conductor.Stop("problem-1", "the maintainer stopped it").Ok.Should().BeTrue();
        // The authority gives the same certificate again: the stored one is the one that was used
        var again = new ReusedApprovals(_problems.Get("problem-1")!.Approval);
        var reusing = Build(again, null, "id", null);

        var result = StartStaged(reusing);

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("already used");
        StartStaged(conductor).Ok.Should().BeTrue("a new approval is a new decision of the maintainer");
    }

    private sealed class ReusedApprovals(SignedApproval approval) : IApprovalSource
    {
        public SignedApproval? For(PlanEntry entry) => approval;
    }

    [Fact]
    public void The_sweep_stops_a_problem_whose_grant_was_lowered_below_the_world_that_is_running()
    {
        var conductor = Conductor();
        var git = Through(conductor, Through(conductor, StartStaged(conductor)));
        git.Mandate!.Mandate.World.Should().Be(World.Release);

        _grants.Max = World.Git;

        conductor.Sweep().Should().Be(1);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
        _problems.Get("problem-1")!.Note.Should().Contain("lowered");
    }

    [Fact]
    public void A_certificate_backs_only_as_many_mandates_as_it_says_whatever_the_control_plane_signs()
    {
        _authority.Adjust = request => request with { MaxMandates = 1 };
        var conductor = Conductor();
        var staging = StartStaged(conductor);
        var id = staging.Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));

        var next = conductor.Decide(id, Grant());

        next.Ok.Should().BeFalse();
        next.Refusal.Should().Contain("no more mandates");
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
    }

    [Fact]
    public void The_dossier_is_kept_before_the_mission_waits_for_the_decision_and_a_refused_hand_over_keeps_nothing()
    {
        var conductor = Conductor();
        var staging = StartStaged(conductor);
        var id = staging.Mission!.Id;

        conductor.HandOver(id, DossierFor(id)).Ok.Should().BeFalse("a mission that did not start cannot hand over");
        _dossiers.Get(id).Should().BeNull();
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id)).Ok.Should().BeTrue();
        _dossiers.Get(id).Should().NotBeNull();
    }

    [Fact]
    public void A_refusal_that_moved_the_mission_says_so_and_a_plain_refusal_does_not()
    {
        var conductor = Conductor();
        var staging = StartStaged(conductor);
        var id = staging.Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));
        _facts.CiGreen = false;
        _grants.Max = World.Release;

        var plain = conductor.Decide("unknown", Grant());
        var sentBack = conductor.Decide(id, Grant());

        plain.Moved.Should().BeFalse();
        sentBack.Ok.Should().BeFalse();
        sentBack.Moved.Should().BeTrue("the check failed and the mission was sent back");
        sentBack.Mission!.SendBacks.Should().Be(1);
    }

    [Fact]
    public void A_stop_on_a_mission_waiting_for_the_controller_revokes_its_mandate_and_a_second_decision_on_it_is_refused()
    {
        var conductor = Conductor();
        var staging = StartStaged(conductor);
        var id = staging.Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));

        conductor.Stop("problem-1", "the maintainer stopped it").Ok.Should().BeTrue();

        _mandates.IsRevoked(staging.Mandate!.Mandate.Id).Should().BeTrue("a stop revokes the mandate even before the controller decided");
        conductor.Decide(id, Grant()).Ok.Should().BeFalse("a mission that ended is not decided");
        _missions.ForProblem("problem-1").Should().ContainSingle("no next-world mandate was issued");
    }

    [Fact]
    public void A_decision_is_applied_once_the_second_grant_on_the_same_mission_issues_nothing()
    {
        var conductor = Conductor();
        var staging = StartStaged(conductor);
        var id = staging.Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id));
        conductor.Decide(id, Grant()).Ok.Should().BeTrue();

        var twice = conductor.Decide(id, Grant());

        twice.Ok.Should().BeFalse();
        _mandates.CountForProblem("problem-1").Should().Be(2, "one for the staging and one for the Git world, no more");
    }

    [Fact]
    public void A_worker_that_names_another_commit_than_the_head_the_forge_reads_gets_no_mandate_for_the_next_world()
    {
        var conductor = Conductor();
        var staging = StartStaged(conductor);
        var id = staging.Mission!.Id;
        conductor.Started(id);
        conductor.HandOver(id, DossierFor(id) with { Commit = "clean-green-commit" });
        _facts.HeadCommit = "the-real-head";

        var decided = conductor.Decide(id, Grant());

        decided.Ok.Should().BeFalse();
        decided.Failures.Should().Contain(PreconditionFailure.ClaimDoesNotMatchFacts);
        _missions.ForProblem("problem-1").Should().ContainSingle("no mandate was issued on the worker's pointer");
    }

    [Fact]
    public void A_certificate_the_authority_withdrew_stops_the_acts_the_crossings_and_the_sweep()
    {
        var conductor = Conductor();
        var git = Through(conductor, StartStaged(conductor));
        conductor.AuthoriseAct(git.Mission!.Id, GatedAct.MergePullRequest, "abc").Allowed.Should().BeTrue();

        _withdrawals.Withdrawn.Add(_problems.Get("problem-1")!.Approval.Certificate.Id);

        conductor.AuthoriseAct(git.Mission.Id, GatedAct.MergePullRequest, "abc").Allowed.Should().BeFalse("a withdrawn approval does not authorise a merge");
        conductor.Sweep().Should().Be(1);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
        _mandates.IsRevoked(git.Mandate!.Mandate.Id).Should().BeTrue();
    }

    [Fact]
    public void The_bound_on_the_mandates_of_a_problem_guards_a_new_mandate_not_the_acts_of_the_last_one_or_the_closing()
    {
        var conductor = Conductor(ConductorOptions.Default with { Loop = new LoopPolicy(3, 3, TimeSpan.FromHours(6)) });
        var release = Through(conductor, Through(conductor, StartStaged(conductor)));
        release.Mandate!.Mandate.World.Should().Be(World.Release);

        conductor.AuthoriseAct(release.Mission!.Id, GatedAct.Deploy, "abc").Allowed.Should().BeTrue("the release mandate is the third and last one allowed, and it may deploy");
        var closed = Through(conductor, release);

        closed.Ok.Should().BeTrue();
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Closed, "a release that used the last mandate is a success, not a failure");
    }

    [Fact]
    public void An_old_approval_does_not_come_back_to_life_after_a_newer_one_was_used()
    {
        var conductor = Conductor();
        var first = StartStaged(conductor);
        var x = first.Mandate!.Approval!;
        conductor.Stop("problem-1", "stopped").Ok.Should().BeTrue();
        StartStaged(conductor).Ok.Should().BeTrue("a new approval Y");
        conductor.Stop("problem-1", "stopped again").Ok.Should().BeTrue();

        var again = StartStaged(Build(new ReusedApprovals(x), null, "id", null));

        again.Ok.Should().BeFalse();
        again.Refusal.Should().Contain("already used");
    }
}
