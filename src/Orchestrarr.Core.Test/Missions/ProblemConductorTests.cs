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
    private readonly FakeApprovals _approvals = new();
    private readonly FakeAudit _audit = new();
    private int _ids;

    public void Dispose() => _signer.Dispose();

    private sealed class FakeFacts : IFactsSource
    {
        public bool CiGreen { get; set; } = true;

        public bool ArtifactProved { get; set; } = true;

        public bool ClaimMatches { get; set; } = true;

        public bool DiffTouchesForbidden { get; set; }

        public bool? WayBack { get; set; } = true;

        public ObservedFacts Facts(string problemId, World world, Dossier? dossier) => new(true, !DiffTouchesForbidden, CiGreen, ArtifactProved, WayBack, ClaimMatches);
    }

    private sealed class FakeStop : IStopSignal
    {
        public bool Engaged { get; set; }
    }

    private sealed class FakeApprovals : IApprovalCheck
    {
        public bool Proved { get; set; } = true;

        public bool IsProved(PlanEntry entry) => Proved;
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

    private ProblemConductor Conductor(ConductorOptions? options = null) =>
        new(_signer, _missions, _mandates, _problems, _dossiers, _facts, new Runners(), _approvals, _stop, _audit, _time, options ?? ConductorOptions.Default, () => $"id-{Interlocked.Increment(ref _ids)}");

    private static PlanEntry Entry(World max = World.Release, string rule = "") => new("problem-1", "shop", "deprecated-dependency", "Replace the removed method", ["example/shop"], max, new MissionBudget(20, 3, 50),
        new Validation("alice", rule.Length == 0 ? null : rule), new DateTimeOffset(2026, 11, 13, 11, 0, 0, TimeSpan.Zero));

    private static Dossier DossierFor(string missionId, bool complex = false) => new(missionId, "done", "replaced the method and adapted the tests", ["sha256:aa"], [], complex, complex ? "a refactor" : null);

    private static ControllerDecision Grant() => new(ControllerDecisionKind.Grant, ["the evidence supports the claim"], null);

    private static ConductResult StartStaged(ProblemConductor conductor, PlanEntry? entry = null) =>
        conductor.Start(entry ?? Entry(), StagingDeclaration.DeclaredStaging, StagingRequirement.FullStack, StagingState.BaselineProven, grant: World.Release, rule: null);

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
        var state = new TestMandateState();

        using var gitRunner = new MandateVerifier(_signer.PublicKey(), "runner-git", World.Git, state, _time);
        using var stagingRunner = new MandateVerifier(_signer.PublicKey(), "runner-staging", World.Staging, state, _time);

        stagingRunner.Accept(git.Mandate!).Should().Be(MandateRefusal.WrongRunner);
        gitRunner.Accept(git.Mandate!).Should().Be(MandateRefusal.None);
    }

    [Fact]
    public void An_application_declared_as_having_no_staging_starts_in_the_git_world()
    {
        var conductor = Conductor();

        var result = conductor.Start(Entry(), StagingDeclaration.DeclaredNoStaging, StagingRequirement.BlankMachine, StagingState.NotDefined, World.Release, null);

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

        var result = conductor.Start(Entry(), declaration, StagingRequirement.FullStack, state, World.Release, null);

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
        var noGrant = conductor.Start(Entry() with { ProblemId = "problem-2" }, StagingDeclaration.DeclaredStaging, StagingRequirement.BlankMachine, StagingState.NotDefined, grant: null, rule: null);

        noGrant.Ok.Should().BeFalse();
        noGrant.Refusal.Should().Contain("grant is off");
        _problems.Get("problem-2").Should().BeNull();
    }

    [Fact]
    public void An_approval_that_is_not_proved_issues_no_mandate_and_a_validation_by_name_alone_is_not_one()
    {
        _approvals.Proved = false;

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
        var start = conductor.Start(Entry(), StagingDeclaration.DeclaredNoStaging, StagingRequirement.BlankMachine, StagingState.NotDefined, World.Release, null);
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
    public void A_standing_rule_issues_the_first_mandate_as_a_standing_rule()
    {
        var conductor = Conductor();

        var result = conductor.Start(Entry(rule: "rule-1"), StagingDeclaration.DeclaredStaging, StagingRequirement.BlankMachine, StagingState.NotDefined, World.Release, rule: World.Git);
        var noRule = conductor.Start(Entry(rule: "rule-1") with { ProblemId = "problem-2" }, StagingDeclaration.DeclaredStaging, StagingRequirement.BlankMachine, StagingState.NotDefined, World.Release, rule: null);

        noRule.Ok.Should().BeFalse("a validation by a rule needs the rule to give a power");

        result.Mandate!.Mandate.Source.Should().Be(MandateSource.StandingRule);
    }

    private sealed class TestMandateState : IMandateState
    {
        private readonly HashSet<string> _used = [];

        public bool KillSwitchEngaged => false;

        public bool TryUse(string nonce) => _used.Add(nonce);

        public bool IsRevoked(string mandateId) => false;
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
        other.Start(Entry() with { ProblemId = "problem-9" }, StagingDeclaration.DeclaredStaging, StagingRequirement.BlankMachine, StagingState.NotDefined, World.Release, null).Ok.Should().BeTrue();
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
        var one = new ProblemConductor(_signer, _missions, _mandates, _problems, _dossiers, _facts, new Runners(), _approvals, _stop, _audit, _time, ConductorOptions.Default, () => $"a-{Interlocked.Increment(ref _ids)}", locks);
        var two = new ProblemConductor(_signer, _missions, _mandates, _problems, _dossiers, _facts, new Runners(), _approvals, _stop, _audit, _time, ConductorOptions.Default, () => $"b-{Interlocked.Increment(ref _ids)}", locks);
        var results = new ConductResult[8];

        Parallel.For(0, results.Length, index => results[index] = StartStaged(index % 2 == 0 ? one : two));

        results.Count(result => result.Ok).Should().Be(1);
        _mandates.CountForProblem("problem-1").Should().Be(1);
    }
}
