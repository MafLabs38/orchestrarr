using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Core.Test.Missions;

public sealed class ProblemConductorTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    // The authority of these tests lets a standing rule reach the release world (the default of the product caps it at the Git world until its mandates are bound to their findings)
    public ProblemConductorTests() => _authority = new TestAuthority(_time, ApprovalPolicy.Default with { MaxRuleWorld = World.Release });

    public void Dispose()
    {
        _signer.Dispose();
        _authority.Dispose();
    }

    private sealed class FakeFacts : IFactsSource
    {
        public bool? CiGreen { get; set; } = true;

        public bool ArtifactProved { get; set; } = true;

        public bool ClaimMatches { get; set; } = true;

        public bool DiffTouchesForbidden { get; set; }

        /// <summary>What the source says about the diff, whatever the paths: the frame of the mission decides on the paths.</summary>
        public bool DiffSaysClean { get; set; } = true;

        /// <summary>The paths changed on the head; null: the source says what the flag says.</summary>
        public IReadOnlyList<string>? ChangedPaths { get; set; }

        public bool UnknownPaths { get; set; }

        /// <summary>What the forge says the base is; null: the base that was asked about.</summary>
        public string? BaseCommit { get; set; }

        private IReadOnlyList<string>? Paths()
        {
            if (UnknownPaths)
            {
                return null;
            }
            return ChangedPaths ?? (DiffTouchesForbidden ? [".github/workflows/ci.yml"] : ["src/Orders.cs", "tests/OrdersTests.cs"]);
        }

        public bool? WayBack { get; set; } = true;

        public bool? MergeMayDeploy { get; set; } = false;

        public bool? ExecutedFiles { get; set; } = false;

        public bool? TagsBlocked { get; set; } = true;

        public bool? BranchSafe { get; set; } = true;

        public bool? Confined { get; set; } = true;


        /// <summary>What the forge says the head is; null: the head is the commit that was asked about.</summary>
        public string? HeadCommit { get; set; }

        public FactsQuery? Last { get; private set; }

        public ValueTask<ObservedFacts> FactsAsync(FactsQuery query, CancellationToken cancellationToken)
        {
            Last = query;
            return ValueTask.FromResult(new ObservedFacts(true, DiffSaysClean, CiGreen, ArtifactProved, WayBack, ClaimMatches, MergeMayDeploy, ExecutedFiles, TagsBlocked, BranchSafe, HeadCommit ?? query.Commit, Confined, Paths(), query.Repository, BaseCommit ?? query.BaseCommit));
        }
    }

    private sealed class FakeStop : IStopSignal
    {
        public bool Engaged { get; set; }
    }

    private sealed class FakeGrants : IGrantSource
    {
        public World? Max { get; set; } = World.Release;

        /// <summary>Applications whose grant was withdrawn.</summary>
        public HashSet<string> Withdrawn { get; } = [];

        /// <summary>An application whose grant cannot be read (the store is unavailable).</summary>
        public string? ThrowFor { get; set; }

        public World? MaxWorldFor(string applicationId)
        {
            if (string.Equals(applicationId, ThrowFor, StringComparison.Ordinal))
            {
                throw new Orchestrarr.Core.Approvals.StorageUnavailableException();
            }
            return Withdrawn.Contains(applicationId) ? null : Max;
        }
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

        public ValueTask<bool> RecordAsync(string eventName, string subject, string? detail, CancellationToken cancellationToken)
        {
            lock (Events)
            {
                Events.Add($"{eventName} {subject} {detail}");
            }
            return ValueTask.FromResult(Writable);
        }
    }

    private sealed class Runners : IRunnerDirectory
    {
        public string RunnerFor(World world, string applicationId) => $"runner-{world}".ToLowerInvariant();
    }

    private ProblemConductor Conductor(ConductorOptions? options = null) => Build(_authority, options, "id", locks: null);

    private ProblemConductor Build(IApprovalSource approvals, ConductorOptions? options, string idPrefix, ProblemLocks? locks) =>
        new(_signer, new MissionStores(_missions, _mandates, _problems, _dossiers), new PowerSources(approvals, _authority.Verifier, _withdrawals, _grants, _stop), new ConductorServices(_facts, new Runners(), _audit, _time, () => $"{idPrefix}-{Interlocked.Increment(ref _ids)}"), options ?? ConductorOptions.Default, locks);

    private static PlanEntry Entry(World max = World.Release, string rule = "") => new("problem-1", "shop", "deprecated-dependency", "Replace the removed method", ["example/shop"], max, new MissionBudget(20, 3, 50),
        new Validation("alice", rule.Length == 0 ? null : rule), new DateTimeOffset(2026, 11, 13, 11, 0, 0, TimeSpan.Zero));

    private static Dossier DossierFor(string missionId, bool complex = false) => new(missionId, "done", "replaced the method and adapted the tests", ["sha256:aa"], [], complex, complex ? "a refactor" : null, Commit: "abc123");

    private static ControllerDecision Grant() => new(ControllerDecisionKind.Grant, ["the evidence supports the claim"], null);

    private static async Task<ConductResult> StartStaged(ProblemConductor conductor, PlanEntry? entry = null) =>
        await conductor.StartAsync(entry ?? Entry(), StagingDeclaration.DeclaredStaging, StagingRequirement.FullStack, StagingState.BaselineProven, Ct);

    private static async Task<ConductResult> Through(ProblemConductor conductor, ConductResult step, bool complex = false)
    {
        var id = step.Mission!.Id;
        (await conductor.StartedAsync(id, Ct)).Ok.Should().BeTrue();
        (await conductor.HandOverAsync(id, DossierFor(id, complex), Ct)).Ok.Should().BeTrue();
        return await conductor.DecideAsync(id, Grant(), Ct);
    }

    [Fact]
    public async Task A_problem_with_a_proven_staging_goes_through_the_three_worlds_each_with_a_mandate_for_its_runner_and_ends_closed()
    {
        var conductor = Conductor();
        var staging = await StartStaged(conductor);
        staging.Mandate!.Mandate.World.Should().Be(World.Staging);
        staging.Mandate.Mandate.Source.Should().Be(MandateSource.PlanValidation);
        staging.Mandate.Mandate.RunnerId.Should().Be("runner-staging");

        var git = await Through(conductor, staging);
        git.Mandate!.Mandate.World.Should().Be(World.Git);
        git.Mandate.Mandate.Source.Should().Be(MandateSource.Controller);
        git.Mandate.Mandate.Acts.Should().BeEquivalentTo([GatedAct.OpenPullRequest, GatedAct.MergePullRequest]);

        var release = await Through(conductor, git);
        release.Mandate!.Mandate.World.Should().Be(World.Release);
        release.Mandate.Mandate.Acts.Should().BeEquivalentTo([GatedAct.Deploy]);
        Enum.GetNames<GatedAct>().Should().NotContain("Rollback", "the way back is a change made through the process, never an act of its own (ADR 0040)");

        var closed = await Through(conductor, release);

        closed.Ok.Should().BeTrue();
        closed.Mission!.State.Should().Be(MissionState.Accepted);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Closed);
        _missions.ForProblem("problem-1").Should().HaveCount(3);
    }

    [Fact]
    public async Task Each_mandate_is_signed_for_its_runner_and_its_world_and_no_other_runner_accepts_it()
    {
        var conductor = Conductor();
        var staging = await StartStaged(conductor);
        var git = await Through(conductor, staging);
        var state = new RunnerState();

        using var gitRunner = new MandateVerifier(_signer.PublicKey(), "runner-git", World.Git, state, _time, _authority.Verifier);
        using var stagingRunner = new MandateVerifier(_signer.PublicKey(), "runner-staging", World.Staging, state, _time, _authority.Verifier);

        stagingRunner.Accept(git.Mandate!).Should().Be(MandateRefusal.WrongRunner);
        gitRunner.Accept(git.Mandate!).Should().Be(MandateRefusal.None);
    }

    [Fact]
    public async Task An_application_declared_as_having_no_staging_starts_in_the_git_world()
    {
        var conductor = Conductor();

        var result = await conductor.StartAsync(Entry(), StagingDeclaration.DeclaredNoStaging, StagingRequirement.BlankMachine, StagingState.NotDefined, Ct);

        result.Mandate!.Mandate.World.Should().Be(World.Git);
    }

    [Theory]
    [InlineData(StagingDeclaration.Undeclared, StagingState.BaselineProven)]
    [InlineData(StagingDeclaration.DeclaredStaging, StagingState.Broken)]
    [InlineData(StagingDeclaration.DeclaredStaging, StagingState.Stale)]
    [InlineData(StagingDeclaration.DeclaredStaging, StagingState.Defined)]
    public async Task A_staging_that_is_not_declared_or_not_proven_refuses_the_first_mandate_and_leaves_nothing_started(StagingDeclaration declaration, StagingState state)
    {
        var conductor = Conductor();

        var result = await conductor.StartAsync(Entry(), declaration, StagingRequirement.FullStack, state, Ct);

        result.Ok.Should().BeFalse();
        result.Failures.Should().Contain(PreconditionFailure.StagingNotUsable);
        _missions.ForProblem("problem-1").Should().BeEmpty();
        _problems.Get("problem-1").Should().BeNull();
    }

    [Fact]
    public async Task A_problem_is_not_started_twice_and_a_grant_that_is_not_given_is_no_power()
    {
        var conductor = Conductor();
        (await StartStaged(conductor)).Ok.Should().BeTrue();

        (await StartStaged(conductor)).Refusal.Should().Contain("already");
        _grants.Max = null;
        var noGrant = await conductor.StartAsync(Entry() with { ProblemId = "problem-2" }, StagingDeclaration.DeclaredStaging, StagingRequirement.BlankMachine, StagingState.NotDefined, Ct);

        noGrant.Ok.Should().BeFalse();
        noGrant.Refusal.Should().Contain("grant is off");
        _problems.Get("problem-2").Should().BeNull();
    }

    [Fact]
    public async Task An_approval_that_is_not_proved_issues_no_mandate_and_a_validation_by_name_alone_is_not_one()
    {
        _authority.Issue = false;

        var result = await StartStaged(Conductor());

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("approval").And.Contain("not proved");
        _missions.ForProblem("problem-1").Should().BeEmpty();
    }

    [Fact]
    public async Task The_global_stop_refuses_a_first_mandate_and_a_next_one_and_ends_the_work_instead_of_issuing_it()
    {
        var conductor = Conductor();
        _stop.Engaged = true;
        (await StartStaged(conductor)).Refusal.Should().Contain("global stop");
        _stop.Engaged = false;
        var staging = await StartStaged(conductor);
        var id = staging.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        _stop.Engaged = true;

        var result = await conductor.DecideAsync(id, Grant(), Ct);

        result.Ok.Should().BeFalse();
        result.Failures.Should().Contain(PreconditionFailure.KillSwitchEngaged);
        _missions.ForProblem("problem-1").Should().ContainSingle("no mandate was issued under the stop");
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
    }

    [Fact]
    public async Task A_problem_validated_up_to_the_git_world_is_closed_there_and_never_given_a_release_mandate()
    {
        var conductor = Conductor();
        var staging = await StartStaged(conductor, Entry(max: World.Git));
        var git = await Through(conductor, staging);
        var id = git.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);

        var closed = await conductor.DecideAsync(id, Grant(), Ct);

        closed.Ok.Should().BeTrue();
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Closed);
        _problems.Get("problem-1")!.Note.Should().Contain("highest that its powers allow");
        _missions.ForProblem("problem-1").Should().HaveCount(2, "no mission was made for the release world");
    }

    [Fact]
    public async Task A_grant_over_a_failed_check_of_the_code_is_never_a_grant_the_mission_is_sent_back_with_the_failure()
    {
        var conductor = Conductor();
        var staging = await StartStaged(conductor);
        var id = staging.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        _facts.DiffTouchesForbidden = true;

        var result = await conductor.DecideAsync(id, Grant(), Ct);

        result.Ok.Should().BeFalse();
        result.Failures.Should().Contain(PreconditionFailure.DiffTouchesForbiddenPath);
        result.Mission!.State.Should().Be(MissionState.Running);
        result.Mission.SendBacks.Should().Be(1);
        _missions.ForProblem("problem-1").Should().ContainSingle("no mandate for the next world was issued");
    }

    [Fact]
    public async Task A_claim_that_the_facts_do_not_support_is_sent_back_before_any_mandate_is_issued()
    {
        var conductor = Conductor();
        var staging = await StartStaged(conductor);
        var id = staging.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        _facts.ClaimMatches = false;

        var result = await conductor.DecideAsync(id, Grant(), Ct);

        result.Failures.Should().Contain(PreconditionFailure.ClaimDoesNotMatchFacts);
    }

    [Fact]
    public async Task A_release_needs_the_green_pipeline_and_the_artifact_that_was_proved_in_staging()
    {
        var conductor = Conductor();
        var git = await Through(conductor, await StartStaged(conductor));
        var release = await Through(conductor, git);
        var id = release.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        _facts.ArtifactProved = false;

        var result = await conductor.DecideAsync(id, Grant(), Ct);

        result.Failures.Should().Contain(PreconditionFailure.ArtifactNotProvedInStaging);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Escalated, "an artifact that is not the one proved is for a person to decide");
    }

    [Fact]
    public async Task A_release_of_an_application_with_no_staging_needs_only_the_pipelines_artifact()
    {
        var conductor = Conductor();
        var start = await conductor.StartAsync(Entry(), StagingDeclaration.DeclaredNoStaging, StagingRequirement.BlankMachine, StagingState.NotDefined, Ct);
        var release = await Through(conductor, start);
        _facts.ArtifactProved = false;

        var closed = await Through(conductor, release);

        closed.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task A_send_back_from_the_controller_continues_in_the_same_world_and_too_many_end_the_problem()
    {
        var conductor = Conductor(ConductorOptions.Default with { Loop = LoopPolicy.Default with { MaxSendBacksPerMission = 1 } });
        var id = (await StartStaged(conductor)).Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        var sendBack = new ControllerDecision(ControllerDecisionKind.SendBack, ["incomplete"], "run the integration tests too");

        var first = await conductor.DecideAsync(id, sendBack, Ct);
        first.Mission!.State.Should().Be(MissionState.Running);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        var second = await conductor.DecideAsync(id, sendBack, Ct);

        second.Mission!.State.Should().Be(MissionState.Ended);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
    }

    [Theory]
    [InlineData(ControllerDecisionKind.End, "the frame was not kept", ProblemState.Failed)]
    [InlineData(ControllerDecisionKind.Escalate, "no fixed version exists", ProblemState.Escalated)]
    public async Task The_controller_can_end_or_escalate_the_mandate_is_revoked_and_an_escalation_waits_for_the_maintainer_instead_of_failing(ControllerDecisionKind kind, string reason, ProblemState expected)
    {
        var conductor = Conductor();
        var staging = await StartStaged(conductor);
        var id = staging.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);

        var result = await conductor.DecideAsync(id, new ControllerDecision(kind, [reason], null), Ct);

        result.Mission!.State.Should().Be(MissionState.Ended);
        _mandates.IsRevoked(staging.Mandate!.Mandate.Id).Should().BeTrue();
        _problems.Get("problem-1")!.State.Should().Be(expected);
        _problems.Get("problem-1")!.Note.Should().Contain(reason);
    }

    [Fact]
    public async Task A_decision_without_reasons_or_on_a_mission_that_did_not_hand_over_is_refused()
    {
        var conductor = Conductor();
        var id = (await StartStaged(conductor)).Mission!.Id;

        (await conductor.DecideAsync(id, Grant(), Ct)).Refusal.Should().Contain("handed over");
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        (await conductor.DecideAsync(id, new ControllerDecision(ControllerDecisionKind.Grant, [], null), Ct)).Refusal.Should().Contain("reasons");
    }

    [Fact]
    public async Task A_dossier_for_another_mission_is_refused_and_a_complex_one_is_marked_on_the_accepted_mission()
    {
        var conductor = Conductor();
        var staging = await StartStaged(conductor);
        var id = staging.Mission!.Id;
        await conductor.StartedAsync(id, Ct);

        (await conductor.HandOverAsync(id, DossierFor("another"), Ct)).Ok.Should().BeFalse();
        await conductor.HandOverAsync(id, DossierFor(id, complex: true), Ct);
        await conductor.DecideAsync(id, Grant(), Ct);

        _missions.Get(id)!.End.Should().Be(MissionEnd.DoneComplex);
    }

    [Fact]
    public async Task The_global_stop_revokes_every_mandate_ends_the_running_mission_and_fails_the_problem()
    {
        var conductor = Conductor();
        var staging = await StartStaged(conductor);
        var git = await Through(conductor, staging);
        await conductor.StartedAsync(git.Mission!.Id, Ct);

        var stopped = await conductor.StopAsync("problem-1", "the global stop", Ct);

        stopped.Ok.Should().BeTrue();
        _missions.Get(git.Mission.Id)!.End.Should().Be(MissionEnd.Stopped);
        _mandates.IsRevoked(git.Mandate!.Mandate.Id).Should().BeTrue();
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
    }

    [Fact]
    public async Task The_loop_of_a_problem_stops_when_it_has_had_its_mandates_or_its_time()
    {
        var conductor = Conductor(ConductorOptions.Default with { Loop = LoopPolicy.Default with { MaxMandatesPerProblem = 2 } });
        var staging = await StartStaged(conductor);
        var git = await Through(conductor, staging);
        var id = git.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);

        var third = await conductor.DecideAsync(id, Grant(), Ct);

        third.Ok.Should().BeFalse();
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
        _problems.Get("problem-1")!.Note.Should().Contain("limit");
    }

    [Fact]
    public async Task A_mission_that_uses_up_its_budget_fails_the_problem_cleanly()
    {
        var conductor = Conductor();
        var id = (await StartStaged(conductor)).Mission!.Id;
        await conductor.StartedAsync(id, Ct);

        var result = await conductor.BudgetExhaustedAsync(id, Ct);

        result.Mission!.End.Should().Be(MissionEnd.Failed);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
    }

    [Fact]
    public async Task A_standing_rule_issues_the_first_mandate_as_a_standing_rule_and_its_ceiling_is_the_certificates()
    {
        _authority.Kind = CertificateKind.Rule;
        _authority.Adjust = request => request with { MaxWorld = World.Git };
        var conductor = Conductor();

        var result = await StartStaged(conductor, Entry(rule: "rule-1"));

        result.Mandate!.Mandate.Source.Should().Be(MandateSource.StandingRule);
        _problems.Get("problem-1")!.HighestAllowed.Should().Be(World.Git, "the rule's certificate stops at the Git world");
    }

    [Fact]
    public async Task A_grant_withdrawn_while_the_problem_is_in_flight_stops_it_at_the_next_crossing_and_a_lowered_grant_closes_it_at_its_new_ceiling()
    {
        var withdrawn = Conductor();
        var staging = await StartStaged(withdrawn);
        var id = staging.Mission!.Id;
        await withdrawn.StartedAsync(id, Ct);
        await withdrawn.HandOverAsync(id, DossierFor(id), Ct);
        _grants.Max = null;

        var stopped = await withdrawn.DecideAsync(id, Grant(), Ct);

        stopped.Mission!.End.Should().Be(MissionEnd.Refused);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
        _problems.Get("problem-1")!.Note.Should().Contain("withdrawn");
        _missions.ForProblem("problem-1").Should().ContainSingle("no next mandate was issued");

        _grants.Max = World.Release;
        var other = Entry() with { ProblemId = "problem-2" };
        var second = await StartStaged(withdrawn, other);
        var secondId = second.Mission!.Id;
        await withdrawn.StartedAsync(secondId, Ct);
        await withdrawn.HandOverAsync(secondId, DossierFor(secondId), Ct);
        _grants.Max = World.Staging;

        var closed = await withdrawn.DecideAsync(secondId, Grant(), Ct);

        closed.Ok.Should().BeTrue();
        _problems.Get("problem-2")!.State.Should().Be(ProblemState.Closed);
        _problems.Get("problem-2")!.Note.Should().Contain("Staging");
    }

    [Fact]
    public async Task The_sweep_stops_a_problem_whose_grant_was_withdrawn_even_when_nothing_else_happens()
    {
        var conductor = Conductor();
        var id = (await StartStaged(conductor)).Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        _grants.Max = null;

        (await conductor.SweepAsync(Ct)).Should().Be(1);

        _problems.Get("problem-1")!.Note.Should().Contain("grant");
    }

    [Fact]
    public async Task A_certificate_that_says_less_than_the_plan_entry_asks_issues_no_mandate_and_a_lower_ceiling_in_the_certificate_wins_over_the_entry()
    {
        _authority.Adjust = request => request with { Repositories = ["example/other"] };
        (await StartStaged(Conductor())).Refusal.Should().Contain("does not cover");

        _authority.Adjust = request => request with { Budget = new MissionBudget(1, 1, 1) };
        (await StartStaged(Conductor())).Refusal.Should().Contain("does not cover");

        _authority.Adjust = request => request with { MaxWorld = World.Git };
        var result = await StartStaged(Conductor(), Entry(max: World.Release));

        result.Ok.Should().BeTrue();
        _problems.Get("problem-1")!.HighestAllowed.Should().Be(World.Git);
    }

    [Fact]
    public async Task Every_mandate_carries_the_certificate_it_falls_inside_and_never_outlives_it()
    {
        _authority.Lifetime = TimeSpan.FromMinutes(10);
        var conductor = Conductor();

        var staging = await StartStaged(conductor);

        staging.Mandate!.Approval.Should().NotBeNull();
        staging.Mandate.Mandate.ApprovalId.Should().Be(staging.Mandate.Approval.Certificate.Id);
        staging.Mandate.Mandate.ExpiresAt.Should().Be(staging.Mandate.Approval.Certificate.ExpiresAt, "the certificate expires before the default lifetime of a mandate");
        _authority.Verifier.Check(staging.Mandate.Approval, staging.Mandate.Mandate).Should().Be(ApprovalRefusal.None);
    }

    [Fact]
    public async Task A_merge_that_may_deploy_is_refused_under_an_approval_that_stops_before_the_release_world_and_unknown_counts_as_deploying()
    {
        _authority.Adjust = request => request with { MaxWorld = World.Git };
        var conductor = Conductor();
        var staging = await StartStaged(conductor, Entry(max: World.Git));
        var id = staging.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        _facts.MergeMayDeploy = true;

        var result = await conductor.DecideAsync(id, Grant(), Ct);

        result.Failures.Should().Contain(PreconditionFailure.MergeMayDeployUnderGitMandate);
        _facts.MergeMayDeploy = null;
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        (await conductor.DecideAsync(id, Grant(), Ct)).Failures.Should().Contain(PreconditionFailure.MergeMayDeployUnderGitMandate);
        _facts.MergeMayDeploy = false;
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        (await conductor.DecideAsync(id, Grant(), Ct)).Mandate!.Mandate.World.Should().Be(World.Git);
    }

    [Fact]
    public async Task A_deployment_that_is_a_merge_with_changes_to_what_the_pipeline_executes_is_escalated_to_the_maintainer_not_sent_back()
    {
        var conductor = Conductor();
        var git = await Through(conductor, await StartStaged(conductor));
        var id = git.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        _facts.MergeMayDeploy = true;
        _facts.ExecutedFiles = true;

        var result = await conductor.DecideAsync(id, Grant(), Ct);

        result.Failures.Should().Contain(PreconditionFailure.ExecutedFilesTouchedWhereMergeDeploys);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Escalated);
        _missions.ForProblem("problem-1").Should().HaveCount(2, "no mandate for the release world was issued");
    }

    [Fact]
    public async Task An_escalated_problem_is_not_started_again_by_accident_and_the_audit_trail_says_so()
    {
        var conductor = Conductor();
        var id = (await StartStaged(conductor)).Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        await conductor.DecideAsync(id, new ControllerDecision(ControllerDecisionKind.Escalate, ["no fixed version exists"], null), Ct);

        _audit.Events.Should().Contain(entry => entry.StartsWith("problem.escalated problem-1", StringComparison.Ordinal));
        var again = new ReusedApprovals(_problems.Get("problem-1")!.Approval);
        (await StartStaged(Build(again, null, "id", null))).Refusal.Should().Contain("already used", "the same approval does not start it again");
        (await StartStaged(conductor)).Ok.Should().BeTrue("a new approval is the maintainer's decision about the escalation");
    }

    [Fact]
    public async Task Every_mandate_decision_send_back_and_end_is_written_to_the_audit_trail()
    {
        var conductor = Conductor();
        var staging = await StartStaged(conductor);
        var id = staging.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        _facts.DiffTouchesForbidden = true;
        await conductor.DecideAsync(id, Grant(), Ct);
        _facts.DiffTouchesForbidden = false;
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        await conductor.DecideAsync(id, new ControllerDecision(ControllerDecisionKind.End, ["the frame was not kept"], null), Ct);

        _audit.Events.Should().Contain(entry => entry.StartsWith("problem.started problem-1", StringComparison.Ordinal))
            .And.Contain(entry => entry.StartsWith("mandate.issued", StringComparison.Ordinal) && entry.Contains("Staging for runner-staging", StringComparison.Ordinal))
            .And.Contain(entry => entry.StartsWith("controller.decided", StringComparison.Ordinal) && entry.Contains("the evidence supports the claim", StringComparison.Ordinal))
            .And.Contain(entry => entry.StartsWith("mission.sent-back", StringComparison.Ordinal))
            .And.Contain(entry => entry.StartsWith("mandate.revoked", StringComparison.Ordinal))
            .And.Contain(entry => entry.StartsWith("mission.ended", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_calls_that_overlap_on_the_same_problem_cannot_both_start_it()
    {
        var conductor = Conductor();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => StartStaged(conductor), Ct)));

        results.Count(result => result.Ok).Should().Be(1);
        _missions.ForProblem("problem-1").Should().ContainSingle();
        _mandates.CountForProblem("problem-1").Should().Be(1);
    }

    [Fact]
    public async Task The_sweep_ends_a_mission_whose_mandate_expired_without_a_runner_and_the_problem_does_not_stay_in_progress_for_ever()
    {
        var conductor = Conductor();
        var id = (await StartStaged(conductor)).Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        _time.Advance(TimeSpan.FromMinutes(61));

        var ended = await conductor.SweepAsync(Ct);

        ended.Should().Be(1);
        _missions.Get(id)!.End.Should().Be(MissionEnd.Stopped);
        _missions.Get(id)!.EndReason.Should().Contain("expired");
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
        _mandates.IsRevoked(_missions.Get(id)!.MandateId!).Should().BeTrue();
    }

    [Fact]
    public async Task The_sweep_stops_every_problem_in_progress_when_the_global_stop_is_engaged_and_after_the_time_bound()
    {
        var conductor = Conductor();
        await StartStaged(conductor);
        (await conductor.SweepAsync(Ct)).Should().Be(0);
        _stop.Engaged = true;

        (await conductor.SweepAsync(Ct)).Should().Be(1);
        _problems.Get("problem-1")!.Note.Should().Contain("global stop");

        _stop.Engaged = false;
        var other = Conductor();
        (await other.StartAsync(Entry() with { ProblemId = "problem-9" }, StagingDeclaration.DeclaredStaging, StagingRequirement.BlankMachine, StagingState.NotDefined, Ct)).Ok.Should().BeTrue();
        _time.Advance(TimeSpan.FromHours(7));
        (await other.SweepAsync(Ct)).Should().Be(1);
        _problems.Get("problem-9")!.Note.Should().Contain("time limit");
    }

    [Fact]
    public async Task A_mission_waiting_for_the_controller_is_not_expired_by_the_sweep()
    {
        var conductor = Conductor();
        var id = (await StartStaged(conductor)).Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        _time.Advance(TimeSpan.FromMinutes(90));

        (await conductor.SweepAsync(Ct)).Should().Be(0);
        _missions.Get(id)!.State.Should().Be(MissionState.HandingOver);
    }

    [Fact]
    public async Task A_release_without_a_way_back_known_is_sent_back_a_deployment_needs_one_before_it_starts()
    {
        var conductor = Conductor();
        var git = await Through(conductor, await StartStaged(conductor));
        var release = await Through(conductor, git);
        var id = release.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        _facts.WayBack = null;

        var result = await conductor.DecideAsync(id, Grant(), Ct);

        result.Failures.Should().Contain(PreconditionFailure.NoWayBackKnown);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.InProgress);
    }

    [Fact]
    public async Task A_power_is_never_granted_on_an_entry_that_the_audit_trail_cannot_write_and_a_power_is_always_withdrawn()
    {
        var conductor = Conductor();
        _audit.Writable = false;
        (await StartStaged(conductor)).Refusal.Should().Contain("audit trail cannot be written");
        _missions.ForProblem("problem-1").Should().BeEmpty();

        _audit.Writable = true;
        var staging = await StartStaged(conductor);
        var id = staging.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        _audit.Writable = false;

        (await conductor.DecideAsync(id, Grant(), Ct)).Refusal.Should().Contain("audit trail cannot be written");
        _missions.ForProblem("problem-1").Should().ContainSingle("no next mandate was issued");

        (await conductor.StopAsync("problem-1", "the global stop", Ct)).Ok.Should().BeTrue("withdrawing a power never waits for the trail");
        _mandates.IsRevoked(staging.Mandate!.Mandate.Id).Should().BeTrue();
    }

    [Fact]
    public async Task Two_conductors_on_the_same_stores_that_share_the_locks_do_not_overlap_on_a_problem()
    {
        var locks = new ProblemLocks();
        var one = Build(_authority, null, "a", locks);
        var two = Build(_authority, null, "b", locks);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() => StartStaged(index % 2 == 0 ? one : two), Ct)));

        results.Count(result => result.Ok).Should().Be(1);
        _mandates.CountForProblem("problem-1").Should().Be(1);
    }

    [Fact]
    public async Task A_start_that_cannot_issue_its_mandate_leaves_nothing_in_progress_and_can_be_tried_again()
    {
        var conductor = Conductor();
        _audit.Writable = false;
        (await StartStaged(conductor)).Ok.Should().BeFalse();
        _problems.Get("problem-1").Should().BeNull("nothing was kept");

        _audit.Writable = true;
        (await StartStaged(conductor)).Ok.Should().BeTrue("the retry is not refused as already in progress");
    }

    [Fact]
    public async Task A_release_that_does_not_verify_is_a_failed_push_the_mission_ends_and_the_problem_starts_again_in_the_first_world_under_the_same_approval()
    {
        var conductor = Conductor();
        var release = (await Through(conductor, await Through(conductor, await StartStaged(conductor)))).Mission!;
        release.World.Should().Be(World.Release);
        _facts.CiGreen = false;
        await conductor.StartedAsync(release.Id, Ct);
        (await conductor.HandOverAsync(release.Id, DossierFor(release.Id), Ct)).Ok.Should().BeTrue();

        var result = await conductor.DecideAsync(release.Id, Grant(), Ct);

        result.Moved.Should().BeTrue("a check failed and the problem moved");
        result.Failures.Should().Contain(PreconditionFailure.CiNotGreenOnExactCommit);
        result.Mission!.World.Should().Be(World.Staging, "the repair goes back through the worlds, a staging first");
        result.Mandate.Should().NotBeNull();
        _missions.Get(release.Id)!.State.Should().Be(MissionState.Ended);
        _mandates.IsRevoked(release.MandateId!).Should().BeTrue("the power never outlives the work");
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.InProgress);
        _mandates.CountForProblem("problem-1").Should().Be(4, "the new mandate counts against the loop's bound and the certificate's count");
    }

    private async Task<ConductResult> ReleaseThatFails(ProblemConductor conductor, bool staged, Action? before = null)
    {
        var step = staged ? await StartStaged(conductor) : await conductor.StartAsync(Entry(), StagingDeclaration.DeclaredNoStaging, StagingRequirement.BlankMachine, StagingState.NotDefined, Ct);
        var release = (await Through(conductor, staged ? await Through(conductor, step) : step)).Mission!;
        _facts.CiGreen = false;
        before?.Invoke();
        await conductor.StartedAsync(release.Id, Ct);
        await conductor.HandOverAsync(release.Id, DossierFor(release.Id), Ct);
        return await conductor.DecideAsync(release.Id, Grant(), Ct);
    }

    [Fact]
    public async Task An_application_with_no_staging_goes_back_to_the_git_world()
    {
        var back = await ReleaseThatFails(Conductor(), staged: false);

        back.Mission!.World.Should().Be(World.Git);
        back.Mandate.Should().NotBeNull();
    }

    [Fact]
    public async Task The_frame_of_the_merge_is_checked_again_before_the_mandate_of_the_way_back_to_the_git_world()
    {
        var frame = await ReleaseThatFails(Conductor(), staged: false, before: () => _facts.TagsBlocked = false);

        frame.Mandate.Should().BeNull("the agent could create tags: no mandate is issued where no crossing checks the frame");
        frame.Failures.Should().Contain(PreconditionFailure.AgentCouldCreateTags);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
    }

    [Fact]
    public async Task A_grant_withdrawn_while_the_release_ran_stops_the_problem_instead_of_issuing_a_mandate_for_the_way_back()
    {
        var result = await ReleaseThatFails(Conductor(), staged: true, before: () => _grants.Withdrawn.Add("shop"));

        result.Mandate.Should().BeNull();
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
    }

    [Fact]
    public async Task A_pipeline_result_that_is_not_known_yet_does_not_restart_the_problem_and_an_artifact_that_is_not_the_one_proved_is_escalated()
    {
        var unknown = await ReleaseThatFails(Conductor(), staged: true, before: () => _facts.CiGreen = null);
        unknown.Failures.Should().Contain(PreconditionFailure.CiResultNotKnown);
        unknown.Mission!.World.Should().Be(World.Release, "the worker may wait and hand over again");
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.InProgress);
    }

    [Fact]
    public async Task An_artifact_that_is_not_the_one_proved_in_staging_after_the_release_is_escalated_to_the_maintainer()
    {
        var result = await ReleaseThatFails(Conductor(), staged: true, before: () => { _facts.CiGreen = true; _facts.ArtifactProved = false; });

        result.Mandate.Should().BeNull();
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Escalated);
    }

    [Fact]
    public async Task Only_a_pipeline_that_is_not_green_or_an_artifact_that_is_not_the_one_proved_sends_the_problem_back_to_the_first_world()
    {
        var result = await ReleaseThatFails(Conductor(), staged: true, before: () => _facts.ClaimMatches = false);

        result.Failures.Should().Contain(PreconditionFailure.ClaimDoesNotMatchFacts);
        result.Mission!.World.Should().Be(World.Release, "restarting does not change a claim that does not match the facts: the mission is sent back where it is");
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.InProgress);
    }

    [Fact]
    public async Task The_controller_that_judges_the_release_failed_sends_the_problem_back_to_the_first_world_where_a_send_back_in_the_release_world_could_only_deploy_the_same_commit_again()
    {
        var conductor = Conductor();
        var release = (await Through(conductor, await Through(conductor, await StartStaged(conductor)))).Mission!;
        await conductor.StartedAsync(release.Id, Ct);
        await conductor.HandOverAsync(release.Id, DossierFor(release.Id), Ct);

        var result = await conductor.DecideAsync(release.Id, new ControllerDecision(ControllerDecisionKind.SendBack, ["the verification of the release does not hold"], "the health check fails"), Ct);

        result.Mission!.World.Should().Be(World.Staging);
        result.Mandate.Should().NotBeNull();
        _missions.Get(release.Id)!.State.Should().Be(MissionState.Ended);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.InProgress);
    }

    [Fact]
    public async Task A_send_back_of_the_controller_in_the_release_world_still_reads_the_facts_so_an_artifact_that_is_not_the_one_proved_goes_to_the_maintainer()
    {
        var conductor = Conductor();
        var release = (await Through(conductor, await Through(conductor, await StartStaged(conductor)))).Mission!;
        await conductor.StartedAsync(release.Id, Ct);
        await conductor.HandOverAsync(release.Id, DossierFor(release.Id), Ct);
        _facts.ArtifactProved = false;

        var result = await conductor.DecideAsync(release.Id, new ControllerDecision(ControllerDecisionKind.SendBack, ["the release does not verify"], "the health check fails"), Ct);

        result.Mandate.Should().BeNull();
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Escalated);
    }

    [Fact]
    public async Task A_send_back_of_the_controller_on_a_pipeline_result_that_is_not_known_yet_stays_in_the_release_world()
    {
        var conductor = Conductor();
        var release = (await Through(conductor, await Through(conductor, await StartStaged(conductor)))).Mission!;
        await conductor.StartedAsync(release.Id, Ct);
        await conductor.HandOverAsync(release.Id, DossierFor(release.Id), Ct);
        _facts.CiGreen = null;

        var result = await conductor.DecideAsync(release.Id, new ControllerDecision(ControllerDecisionKind.SendBack, ["the release does not verify"], "wait for the pipeline"), Ct);

        result.Mission!.World.Should().Be(World.Release);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.InProgress);
    }

    [Fact]
    public async Task A_release_that_the_controller_judged_failed_goes_back_to_the_first_world_even_when_the_claim_does_not_match_the_facts()
    {
        var conductor = Conductor();
        var release = (await Through(conductor, await Through(conductor, await StartStaged(conductor)))).Mission!;
        await conductor.StartedAsync(release.Id, Ct);
        await conductor.HandOverAsync(release.Id, DossierFor(release.Id), Ct);
        _facts.ClaimMatches = false;

        var result = await conductor.DecideAsync(release.Id, new ControllerDecision(ControllerDecisionKind.SendBack, ["the release does not verify"], "fix the claim"), Ct);

        result.Mission!.World.Should().Be(World.Staging, "the release failed whatever the dossier claimed, and a send-back in the release world could only deploy the same commit again");
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.InProgress);
    }

    [Fact]
    public async Task A_release_that_failed_under_a_standing_rule_is_escalated_because_nobody_approved_that_problem()
    {
        _authority.Kind = CertificateKind.Rule;
        var conductor = Conductor();
        var release = (await Through(conductor, await Through(conductor, await StartStaged(conductor, Entry(rule: "rule-1"))))).Mission!;
        release.World.Should().Be(World.Release);
        _facts.CiGreen = false;
        await conductor.StartedAsync(release.Id, Ct);
        await conductor.HandOverAsync(release.Id, DossierFor(release.Id), Ct);

        var result = await conductor.DecideAsync(release.Id, Grant(), Ct);

        result.Mandate.Should().BeNull();
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Escalated);
    }

    [Fact]
    public async Task When_the_loop_is_over_its_bound_the_way_back_ends_the_problem_and_the_answer_still_carries_the_mission_that_ended()
    {
        var conductor = Conductor(new ConductorOptions(TimeSpan.FromMinutes(60), ForbiddenPathsDefaults.Floor, new LoopPolicy(3, 3, TimeSpan.FromHours(6))));

        var result = await ReleaseThatFails(conductor, staged: true);

        result.Mandate.Should().BeNull("the third mandate was the last the loop allowed");
        result.Moved.Should().BeTrue("the release mission ended whatever Open said");
        result.Mission!.World.Should().Be(World.Release);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
    }

    [Fact]
    public async Task The_sweep_stops_a_problem_that_a_crash_left_in_progress_with_every_mission_over()
    {
        var conductor = Conductor();
        var id = (await StartStaged(conductor)).Mission!.Id;
        _missions.Save(_missions.Get(id)! with { State = MissionState.Ended, End = MissionEnd.Stopped, EndReason = "crashed" });

        (await conductor.SweepAsync(Ct)).Should().Be(1);

        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
        _problems.Get("problem-1")!.Note.Should().Contain("half done");
    }

    [Fact]
    public async Task A_problem_on_two_repositories_is_refused_because_it_is_as_many_problems_as_repositories()
    {
        var result = await StartStaged(Conductor(), Entry() with { Repositories = ["example/shop", "example/api"] });

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("exactly one repository");
        _problems.Get("problem-1").Should().BeNull("nothing is left in progress");
    }

    [Fact]
    public async Task A_certificate_for_another_application_or_another_class_issues_no_mandate()
    {
        _authority.Adjust = request => request with { ApplicationId = "other-app" };
        (await StartStaged(Conductor())).Refusal.Should().Contain("does not cover");

        _authority.Adjust = null;
        _authority.Kind = CertificateKind.Rule;
        _authority.Adjust = request => request with { Subject = "secret-exposed" };
        (await StartStaged(Conductor(), Entry(World.Git))).Refusal.Should().Contain("does not cover");
    }

    [Fact]
    public async Task The_mandates_carry_the_forbidden_paths_of_the_certificate_and_the_application_and_class_of_the_problem()
    {
        _authority.Adjust = request => request with { ForbiddenPaths = ["infra/prod/**"] };
        var conductor = Conductor();

        var staging = await StartStaged(conductor);

        staging.Mandate!.Mandate.Frame.ForbiddenPaths.Should().Contain("infra/prod/**").And.Contain(".github/**");
        staging.Mandate.Mandate.ApplicationId.Should().Be("shop");
        staging.Mandate.Mandate.ProblemClass.Should().Be("deprecated-dependency");
    }

    [Fact]
    public async Task A_stop_that_moves_the_problem_to_failed_is_a_stop_that_worked_even_with_no_mission_running()
    {
        var conductor = Conductor();
        var id = (await StartStaged(conductor)).Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        await conductor.DecideAsync(id, new ControllerDecision(ControllerDecisionKind.SendBack, ["incomplete"], "more tests"), Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        var ended = await conductor.DecideAsync(id, new ControllerDecision(ControllerDecisionKind.End, ["frame broken"], null), Ct);
        ended.Ok.Should().BeTrue();

        (await conductor.StopAsync("problem-1", "x", Ct)).Ok.Should().BeFalse("the problem is no longer in progress");
    }

    private static async Task<ConductResult> StartUnstaged(ProblemConductor conductor, PlanEntry? entry = null) =>
        await conductor.StartAsync(entry ?? Entry(), StagingDeclaration.DeclaredNoStaging, StagingRequirement.BlankMachine, StagingState.NotDefined, Ct);

    [Fact]
    public async Task The_mandate_of_an_accepted_mission_is_revoked_at_every_crossing_and_when_the_problem_closes()
    {
        var conductor = Conductor();
        var staging = await StartStaged(conductor);
        var git = await Through(conductor, staging);
        _mandates.IsRevoked(staging.Mandate!.Mandate.Id).Should().BeTrue("the power never outlives the work");
        var release = await Through(conductor, git);
        _mandates.IsRevoked(git.Mandate!.Mandate.Id).Should().BeTrue();

        (await Through(conductor, release)).Ok.Should().BeTrue();

        _mandates.IsRevoked(release.Mandate!.Mandate.Id).Should().BeTrue("a closed problem leaves no mandate in force");
    }

    [Fact]
    public async Task A_problem_that_stops_at_the_git_world_revokes_that_mandate_when_it_closes_and_a_send_back_that_ends_the_mission_revokes_it_too()
    {
        var conductor = Conductor();
        var git = await StartUnstaged(conductor, Entry(World.Git) with { ProblemId = "problem-1" });
        git.Mandate!.Mandate.World.Should().Be(World.Git);
        (await Through(conductor, git)).Ok.Should().BeTrue();
        _mandates.IsRevoked(git.Mandate.Mandate.Id).Should().BeTrue();

        var looping = await StartStaged(conductor, Entry() with { ProblemId = "problem-2" });
        var id = looping.Mission!.Id;
        for (var round = 0; round < 4 && _missions.Get(id)!.State != MissionState.Ended; round++)
        {
            await conductor.StartedAsync(id, Ct);
            await conductor.HandOverAsync(id, DossierFor(id), Ct);
            await conductor.DecideAsync(id, new ControllerDecision(ControllerDecisionKind.SendBack, ["not yet"], "more tests"), Ct);
        }

        _missions.Get(id)!.State.Should().Be(MissionState.Ended);
        _mandates.IsRevoked(looping.Mandate!.Mandate.Id).Should().BeTrue();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(null, true)]
    [InlineData(false, false)]
    public async Task A_start_in_the_git_world_checks_the_frame_of_the_merge_because_no_crossing_will(bool? mayDeploy, bool tagsBlocked)
    {
        _facts.MergeMayDeploy = mayDeploy;
        _facts.TagsBlocked = tagsBlocked;

        var result = await StartUnstaged(Conductor(), Entry(World.Git));

        result.Ok.Should().BeFalse();
        result.Failures.Should().Contain(mayDeploy == false ? PreconditionFailure.AgentCouldCreateTags : PreconditionFailure.MergeMayDeployUnderGitMandate);
        _missions.ForProblem("problem-1").Should().BeEmpty();
        _problems.Get("problem-1").Should().BeNull();
    }

    [Fact]
    public async Task A_start_in_the_git_world_is_allowed_when_the_merge_does_not_deploy_and_tags_are_blocked_whatever_the_approval_reaches()
    {
        _facts.MergeMayDeploy = false;
        (await StartUnstaged(Conductor(), Entry(World.Git))).Ok.Should().BeTrue();

        _facts.MergeMayDeploy = true;
        (await StartUnstaged(Conductor(), Entry() with { ProblemId = "problem-2" })).Failures.Should().Contain(PreconditionFailure.MergeMayDeployUnderGitMandate, "a merge that deploys is a release-world act, even when the approval reaches the release world");
    }

    [Fact]
    public async Task A_merge_that_may_deploy_is_refused_under_a_git_mandate_whatever_the_certificate_and_the_grant_reach()
    {
        // The certificate reaches the release world, the grant of the application was lowered to the Git world, and the merge may deploy
        _grants.Max = World.Git;
        _facts.MergeMayDeploy = true;
        var conductor = Conductor();
        var staging = await StartStaged(conductor);
        var id = staging.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);

        var decided = await conductor.DecideAsync(id, Grant(), Ct);

        decided.Ok.Should().BeFalse();
        decided.Failures.Should().Contain(PreconditionFailure.MergeMayDeployUnderGitMandate);
        _missions.ForProblem("problem-1").Should().ContainSingle("no Git mandate was issued");
    }

    [Fact]
    public async Task The_act_gateway_allows_a_merge_only_when_the_facts_of_this_moment_on_this_commit_hold_and_writes_it_down()
    {
        var conductor = Conductor();
        var git = await Through(conductor, await StartStaged(conductor));
        var mission = git.Mission!.Id;

        var allowed = await conductor.AuthoriseActAsync(mission, GatedAct.MergePullRequest, "abc123", Ct, repository: "example/shop", baseCommit: "base-1");

        allowed.Allowed.Should().BeTrue();
        _audit.Events.Should().Contain(entry => entry.StartsWith("act.authorised") && entry.Contains("MergePullRequest on abc123"));

        _facts.CiGreen = false;
        var refused = await conductor.AuthoriseActAsync(mission, GatedAct.MergePullRequest, "def456", Ct, repository: "example/shop", baseCommit: "base-1");

        refused.Allowed.Should().BeFalse();
        refused.Failures.Should().Contain(PreconditionFailure.CiNotGreenOnExactCommit);
        _audit.Events.Should().Contain(entry => entry.StartsWith("act.refused") && entry.Contains("def456"));
    }

    [Fact]
    public async Task The_act_gateway_refuses_a_diff_outside_the_frame_and_escalates_a_change_to_executed_files_where_the_merge_deploys()
    {
        var conductor = Conductor();
        var git = await Through(conductor, await StartStaged(conductor));
        var mission = git.Mission!.Id;

        _facts.DiffTouchesForbidden = true;
        (await conductor.AuthoriseActAsync(mission, GatedAct.MergePullRequest, "abc", Ct, repository: "example/shop", baseCommit: "base-1")).Failures.Should().Contain(PreconditionFailure.DiffTouchesForbiddenPath);
        _facts.DiffTouchesForbidden = false;
        _facts.MergeMayDeploy = true;

        var deploying = await conductor.AuthoriseActAsync(mission, GatedAct.MergePullRequest, "abc", Ct, repository: "example/shop", baseCommit: "base-1");

        deploying.Allowed.Should().BeFalse();
        deploying.Failures.Should().Contain(PreconditionFailure.MergeMayDeployUnderGitMandate, "a merge that deploys is not a Git act");
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.InProgress);
    }

    [Fact]
    public async Task In_the_release_world_a_deployment_that_is_a_merge_with_executed_files_touched_is_refused_and_escalated()
    {
        var conductor = Conductor();
        var release = await Through(conductor, await Through(conductor, await StartStaged(conductor)));
        _facts.MergeMayDeploy = true;
        _facts.ExecutedFiles = true;

        var executed = await conductor.AuthoriseActAsync(release.Mission!.Id, GatedAct.Deploy, "abc123", Ct, repository: "example/shop", baseCommit: "base-1");

        executed.Allowed.Should().BeFalse();
        executed.Failures.Should().Contain(PreconditionFailure.ExecutedFilesTouchedWhereMergeDeploys);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Escalated);
        _mandates.IsRevoked(release.Mandate!.Mandate.Id).Should().BeTrue("the mission ended, a person decides");
    }

    [Fact]
    public async Task A_merge_or_a_deployment_names_its_repository_commit_and_base()
    {
        var conductor = Conductor();
        var git = await Through(conductor, await StartStaged(conductor));
        var id = git.Mission!.Id;

        (await conductor.AuthoriseActAsync(id, GatedAct.MergePullRequest, "abc", Ct, repository: null, baseCommit: "base-1")).Refusal.Should().Contain("repository, commit and base");
        (await conductor.AuthoriseActAsync(id, GatedAct.MergePullRequest, "abc", Ct, repository: "example/shop", baseCommit: null)).Allowed.Should().BeFalse();
        (await conductor.AuthoriseActAsync(id, GatedAct.MergePullRequest, "abc", Ct, repository: "example/other", baseCommit: "base-1")).Refusal.Should().Contain("not in the frame");
        var allowed = await conductor.AuthoriseActAsync(id, GatedAct.MergePullRequest, "abc", Ct, repository: "example/shop", baseCommit: "base-1");
        allowed.Allowed.Should().BeTrue();
        (allowed.Repository, allowed.Commit, allowed.BaseCommit).Should().Be(("example/shop", "abc", "base-1"));
        _facts.BaseCommit = "the-base-moved";
        (await conductor.AuthoriseActAsync(id, GatedAct.MergePullRequest, "abc", Ct, repository: "example/shop", baseCommit: "base-1")).Failures.Should().Contain(PreconditionFailure.ClaimDoesNotMatchFacts, "the base the source read is not the base that was named");
    }

    [Fact]
    public async Task Once_the_worker_has_handed_over_no_act_is_authorised_on_the_mandate_so_a_commit_pushed_after_the_claim_cannot_be_merged()
    {
        var conductor = Conductor();
        var git = await Through(conductor, await StartStaged(conductor));
        var id = git.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        (await conductor.AuthoriseActAsync(id, GatedAct.MergePullRequest, "abc", Ct, repository: "example/shop", baseCommit: "base-1")).Allowed.Should().BeTrue("the mission is running");

        await conductor.HandOverAsync(id, DossierFor(id), Ct);

        (await conductor.AuthoriseActAsync(id, GatedAct.MergePullRequest, "other", Ct, repository: "example/shop", baseCommit: "base-1")).Refusal.Should().Contain("no mandate in force", "the dossier is the frozen claim that the controller judges");
    }

    [Fact]
    public async Task A_deployment_is_on_the_commit_that_the_controller_accepted_and_no_other()
    {
        var conductor = Conductor();
        var release = await Through(conductor, await Through(conductor, await StartStaged(conductor)));

        var other = await conductor.AuthoriseActAsync(release.Mission!.Id, GatedAct.Deploy, "abc", Ct, repository: "example/shop", baseCommit: "base-1");

        other.Allowed.Should().BeFalse();
        other.Refusal.Should().Contain("not the one that the controller accepted", "the dossier of the Git world names abc123, and a commit pushed after the crossing was read by nobody");
        _audit.Events.Should().Contain(entry => entry.StartsWith("act.refused") && entry.Contains("not the commit that the controller accepted"));
        (await conductor.AuthoriseActAsync(release.Mission.Id, GatedAct.Deploy, "abc123", Ct, repository: "example/shop", baseCommit: "base-1")).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task The_act_gateway_refuses_an_act_the_mandate_does_not_list_a_revoked_or_expired_mandate_a_stop_and_a_grant_lowered_below_the_world()
    {
        var conductor = Conductor();
        var git = await Through(conductor, await StartStaged(conductor));
        var mission = git.Mission!.Id;

        (await conductor.AuthoriseActAsync(mission, GatedAct.Deploy, "abc", Ct, repository: "example/shop", baseCommit: "base-1")).Refusal.Should().Contain("does not list");
        (await conductor.AuthoriseActAsync("unknown", GatedAct.MergePullRequest, "abc", Ct, repository: "example/shop", baseCommit: "base-1")).Refusal.Should().Contain("unknown");
        _grants.Max = World.Staging;
        (await conductor.AuthoriseActAsync(mission, GatedAct.MergePullRequest, "abc", Ct, repository: "example/shop", baseCommit: "base-1")).Refusal.Should().Contain("no longer reach");
        _grants.Max = World.Release;
        _stop.Engaged = true;
        (await conductor.AuthoriseActAsync(mission, GatedAct.MergePullRequest, "abc", Ct, repository: "example/shop", baseCommit: "base-1")).Refusal.Should().Contain("global stop");
        _stop.Engaged = false;
        _time.Advance(TimeSpan.FromMinutes(61));
        (await conductor.AuthoriseActAsync(mission, GatedAct.MergePullRequest, "abc", Ct, repository: "example/shop", baseCommit: "base-1")).Refusal.Should().Contain("expired");
        _mandates.Revoke(git.Mandate!.Mandate.Id);
        (await conductor.AuthoriseActAsync(mission, GatedAct.MergePullRequest, "abc", Ct, repository: "example/shop", baseCommit: "base-1")).Refusal.Should().Contain("no mandate in force");
    }

    [Fact]
    public async Task An_act_that_does_not_change_what_runs_needs_only_the_mandate_and_a_failing_trail_refuses_the_power()
    {
        var conductor = Conductor();
        var git = await Through(conductor, await StartStaged(conductor));
        _facts.CiGreen = false;

        (await conductor.AuthoriseActAsync(git.Mission!.Id, GatedAct.OpenPullRequest, null, Ct, repository: "example/shop")).Allowed.Should().BeTrue("opening a pull request changes nothing that runs");
        (await conductor.AuthoriseActAsync(git.Mission.Id, GatedAct.OpenPullRequest, null, Ct, repository: "example/other")).Refusal.Should().Contain("not in the frame", "the gateway opens it where it is told, so the repository is checked too");
        (await conductor.AuthoriseActAsync(git.Mission.Id, GatedAct.OpenPullRequest, null, Ct)).Refusal.Should().Contain("named repository");

        _audit.Writable = false;
        _facts.CiGreen = true;
        (await conductor.AuthoriseActAsync(git.Mission.Id, GatedAct.MergePullRequest, "abc", Ct, repository: "example/shop", baseCommit: "base-1")).Refusal.Should().Contain("audit trail");
    }

    [Fact]
    public async Task An_approval_is_for_one_run_a_stopped_problem_is_not_started_again_on_it_but_a_new_approval_starts_it()
    {
        var conductor = Conductor();
        (await StartStaged(conductor)).Ok.Should().BeTrue();
        (await conductor.StopAsync("problem-1", "the maintainer stopped it", Ct)).Ok.Should().BeTrue();
        // The authority gives the same certificate again: the stored one is the one that was used
        var again = new ReusedApprovals(_problems.Get("problem-1")!.Approval);
        var reusing = Build(again, null, "id", null);

        var result = await StartStaged(reusing);

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("already used");
        (await StartStaged(conductor)).Ok.Should().BeTrue("a new approval is a new decision of the maintainer");
    }

    private sealed class ReusedApprovals(SignedApproval approval) : IApprovalSource
    {
        public SignedApproval? For(PlanEntry entry) => approval;
    }

    [Fact]
    public async Task The_sweep_stops_a_problem_whose_grant_was_lowered_below_the_world_that_is_running()
    {
        var conductor = Conductor();
        var git = await Through(conductor, await Through(conductor, await StartStaged(conductor)));
        git.Mandate!.Mandate.World.Should().Be(World.Release);

        _grants.Max = World.Git;

        (await conductor.SweepAsync(Ct)).Should().Be(1);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
        _problems.Get("problem-1")!.Note.Should().Contain("lowered");
    }

    [Fact]
    public async Task A_certificate_backs_only_as_many_mandates_as_it_says_whatever_the_control_plane_signs()
    {
        _authority.Adjust = request => request with { MaxMandates = 1 };
        var conductor = Conductor();
        var staging = await StartStaged(conductor);
        var id = staging.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);

        var next = await conductor.DecideAsync(id, Grant(), Ct);

        next.Ok.Should().BeFalse();
        next.Refusal.Should().Contain("no more mandates");
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
    }

    [Fact]
    public async Task The_dossier_is_kept_before_the_mission_waits_for_the_decision_and_a_refused_hand_over_keeps_nothing()
    {
        var conductor = Conductor();
        var staging = await StartStaged(conductor);
        var id = staging.Mission!.Id;

        (await conductor.HandOverAsync(id, DossierFor(id), Ct)).Ok.Should().BeFalse("a mission that did not start cannot hand over");
        _dossiers.Get(id).Should().BeNull();
        await conductor.StartedAsync(id, Ct);
        (await conductor.HandOverAsync(id, DossierFor(id), Ct)).Ok.Should().BeTrue();
        _dossiers.Get(id).Should().NotBeNull();
    }

    [Fact]
    public async Task A_refusal_that_moved_the_mission_says_so_and_a_plain_refusal_does_not()
    {
        var conductor = Conductor();
        var staging = await StartStaged(conductor);
        var id = staging.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        _facts.CiGreen = false;
        _grants.Max = World.Release;

        var plain = await conductor.DecideAsync("unknown", Grant(), Ct);
        var sentBack = await conductor.DecideAsync(id, Grant(), Ct);

        plain.Moved.Should().BeFalse();
        sentBack.Ok.Should().BeFalse();
        sentBack.Moved.Should().BeTrue("the check failed and the mission was sent back");
        sentBack.Mission!.SendBacks.Should().Be(1);
    }

    [Fact]
    public async Task A_stop_on_a_mission_waiting_for_the_controller_revokes_its_mandate_and_a_second_decision_on_it_is_refused()
    {
        var conductor = Conductor();
        var staging = await StartStaged(conductor);
        var id = staging.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);

        (await conductor.StopAsync("problem-1", "the maintainer stopped it", Ct)).Ok.Should().BeTrue();

        _mandates.IsRevoked(staging.Mandate!.Mandate.Id).Should().BeTrue("a stop revokes the mandate even before the controller decided");
        (await conductor.DecideAsync(id, Grant(), Ct)).Ok.Should().BeFalse("a mission that ended is not decided");
        _missions.ForProblem("problem-1").Should().ContainSingle("no next-world mandate was issued");
    }

    [Fact]
    public async Task A_decision_is_applied_once_the_second_grant_on_the_same_mission_issues_nothing()
    {
        var conductor = Conductor();
        var staging = await StartStaged(conductor);
        var id = staging.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        (await conductor.DecideAsync(id, Grant(), Ct)).Ok.Should().BeTrue();

        var twice = await conductor.DecideAsync(id, Grant(), Ct);

        twice.Ok.Should().BeFalse();
        _mandates.CountForProblem("problem-1").Should().Be(2, "one for the staging and one for the Git world, no more");
    }

    [Fact]
    public async Task A_worker_that_names_another_commit_than_the_head_the_forge_reads_gets_no_mandate_for_the_next_world()
    {
        var conductor = Conductor();
        var staging = await StartStaged(conductor);
        var id = staging.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id) with { Commit = "clean-green-commit" }, Ct);
        _facts.HeadCommit = "the-real-head";

        var decided = await conductor.DecideAsync(id, Grant(), Ct);

        decided.Ok.Should().BeFalse();
        decided.Failures.Should().Contain(PreconditionFailure.ClaimDoesNotMatchFacts);
        _missions.ForProblem("problem-1").Should().ContainSingle("no mandate was issued on the worker's pointer");
    }

    [Fact]
    public async Task A_certificate_the_authority_withdrew_stops_the_acts_the_crossings_and_the_sweep()
    {
        var conductor = Conductor();
        var git = await Through(conductor, await StartStaged(conductor));
        (await conductor.AuthoriseActAsync(git.Mission!.Id, GatedAct.MergePullRequest, "abc", Ct, repository: "example/shop", baseCommit: "base-1")).Allowed.Should().BeTrue();

        _withdrawals.Withdrawn.Add(_problems.Get("problem-1")!.Approval.Certificate.Id);

        (await conductor.AuthoriseActAsync(git.Mission.Id, GatedAct.MergePullRequest, "abc", Ct, repository: "example/shop", baseCommit: "base-1")).Allowed.Should().BeFalse("a withdrawn approval does not authorise a merge");
        (await conductor.SweepAsync(Ct)).Should().Be(1);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Failed);
        _mandates.IsRevoked(git.Mandate!.Mandate.Id).Should().BeTrue();
    }

    [Fact]
    public async Task The_bound_on_the_mandates_of_a_problem_guards_a_new_mandate_not_the_acts_of_the_last_one_or_the_closing()
    {
        var conductor = Conductor(ConductorOptions.Default with { Loop = new LoopPolicy(3, 3, TimeSpan.FromHours(6)) });
        var release = await Through(conductor, await Through(conductor, await StartStaged(conductor)));
        release.Mandate!.Mandate.World.Should().Be(World.Release);

        (await conductor.AuthoriseActAsync(release.Mission!.Id, GatedAct.Deploy, "abc123", Ct, repository: "example/shop", baseCommit: "base-1")).Allowed.Should().BeTrue("the release mandate is the third and last one allowed, and it may deploy");
        var closed = await Through(conductor, release);

        closed.Ok.Should().BeTrue();
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.Closed, "a release that used the last mandate is a success, not a failure");
    }

    [Fact]
    public async Task An_old_approval_does_not_come_back_to_life_after_a_newer_one_was_used()
    {
        var conductor = Conductor();
        var first = await StartStaged(conductor);
        var x = first.Mandate!.Approval!;
        (await conductor.StopAsync("problem-1", "stopped", Ct)).Ok.Should().BeTrue();
        (await StartStaged(conductor)).Ok.Should().BeTrue("a new approval Y");
        (await conductor.StopAsync("problem-1", "stopped again", Ct)).Ok.Should().BeTrue();

        var again = await StartStaged(Build(new ReusedApprovals(x), null, "id", null));

        again.Ok.Should().BeFalse();
        again.Refusal.Should().Contain("already used");
    }

    [Fact]
    public async Task A_merge_or_a_deployment_is_authorised_on_a_named_commit_and_the_answer_names_it()
    {
        var conductor = Conductor();
        var git = await Through(conductor, await StartStaged(conductor));

        (await conductor.AuthoriseActAsync(git.Mission!.Id, GatedAct.MergePullRequest, null, Ct, repository: "example/shop", baseCommit: "base-1")).Refusal.Should().Contain("named repository, commit and base");
        (await conductor.AuthoriseActAsync(git.Mission.Id, GatedAct.MergePullRequest, " ", Ct, repository: "example/shop", baseCommit: "base-1")).Allowed.Should().BeFalse();
        var allowed = await conductor.AuthoriseActAsync(git.Mission.Id, GatedAct.MergePullRequest, "abc123", Ct, repository: "example/shop", baseCommit: "base-1");

        allowed.Allowed.Should().BeTrue();
        allowed.Commit.Should().Be("abc123", "the gateway merges that commit and no other");
        _facts.HeadCommit = "pushed-after-the-check";
        (await conductor.AuthoriseActAsync(git.Mission.Id, GatedAct.MergePullRequest, "abc123", Ct, repository: "example/shop", baseCommit: "base-1")).Failures.Should().Contain(PreconditionFailure.ClaimDoesNotMatchFacts, "the head moved since the commit that is named");
    }

    [Fact]
    public async Task A_dossier_with_no_commit_gives_no_mandate_past_the_staging_world_because_unknown_counts_as_failing()
    {
        var conductor = Conductor();
        var staging = await StartStaged(conductor);
        var id = staging.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id) with { Commit = null }, Ct);

        var decided = await conductor.DecideAsync(id, Grant(), Ct);

        decided.Ok.Should().BeFalse();
        decided.Failures.Should().Contain(PreconditionFailure.ClaimDoesNotMatchFacts);
    }

    [Fact]
    public async Task A_certificate_that_backs_no_more_mandates_says_so_and_is_not_the_loop_bound()
    {
        _authority.Adjust = request => request with { MaxMandates = 1 };
        var conductor = Conductor();
        var id = (await StartStaged(conductor)).Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);

        var next = await conductor.DecideAsync(id, Grant(), Ct);

        next.Failures.Should().Equal(PreconditionFailure.ApprovalBacksNoMoreMandates);
    }

    [Fact]
    public async Task A_branch_the_identity_of_which_is_not_confined_gives_no_git_mandate_at_the_crossing()
    {
        _facts.Confined = null;
        var conductor = Conductor();
        var id = (await StartStaged(conductor)).Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);

        var decided = await conductor.DecideAsync(id, Grant(), Ct);

        decided.Ok.Should().BeFalse();
        decided.Failures.Should().Contain(PreconditionFailure.AgentIdentityNotConfined);
    }

    [Fact]
    public async Task The_forbidden_paths_of_the_mission_decide_the_diff_so_the_floor_of_the_certificate_holds_when_the_options_list_is_empty()
    {
        var conductor = Conductor(ConductorOptions.Default with { ForbiddenPaths = [] });
        var staging = await StartStaged(conductor);
        staging.Mandate!.Mandate.Frame.ForbiddenPaths.Should().Contain(".github/**", "the certificate's floor is in the frame whatever the options say");
        var id = staging.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        _facts.ChangedPaths = [".github/workflows/ci.yml"];

        var decided = await conductor.DecideAsync(id, Grant(), Ct);

        decided.Failures.Should().Contain(PreconditionFailure.DiffTouchesForbiddenPath, "the source said the diff is clean, and the frame says otherwise");
    }

    [Fact]
    public async Task A_rename_out_of_a_forbidden_path_counts_by_its_old_path_so_a_pipeline_cannot_be_moved_out_of_the_floor()
    {
        var conductor = Conductor();
        var id = (await StartStaged(conductor)).Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        _facts.DiffSaysClean = true;
        _facts.ChangedPaths = [".github/workflows/codeql.yml", "src/codeql.yml"];

        (await conductor.DecideAsync(id, Grant(), Ct)).Failures.Should().Contain(PreconditionFailure.DiffTouchesForbiddenPath, "the source lists both sides of the rename, and the old path is in the floor");
    }

    [Fact]
    public async Task A_diff_whose_paths_are_not_known_is_not_clean()
    {
        var conductor = Conductor();
        var id = (await StartStaged(conductor)).Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        _facts.ChangedPaths = null;
        _facts.DiffSaysClean = true;

        // The fake falls back to a clean list when none is set: an unknown list is a source that returns none
        _facts.UnknownPaths = true;

        (await conductor.DecideAsync(id, Grant(), Ct)).Failures.Should().Contain(PreconditionFailure.DiffTouchesForbiddenPath);
    }

    [Fact]
    public async Task One_run_that_cannot_be_read_never_keeps_the_others_from_being_stopped()
    {
        var conductor = Conductor();
        (await StartStaged(conductor)).Ok.Should().BeTrue();
        (await StartStaged(conductor, Entry() with { ProblemId = "problem-2", ApplicationId = "blog" })).Ok.Should().BeTrue();
        _grants.ThrowFor = "shop";
        _grants.Withdrawn.Add("blog");

        (await conductor.SweepAsync(Ct)).Should().Be(1, "the second problem is stopped although the first cannot be read");

        _problems.Get("problem-2")!.State.Should().Be(ProblemState.Failed);
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.InProgress);
        _audit.Events.Should().Contain(entry => entry.StartsWith("sweep.failed problem-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_problem_started_again_on_a_new_approval_starts_a_new_count_of_mandates()
    {
        var conductor = Conductor(ConductorOptions.Default with { Loop = new LoopPolicy(3, 3, TimeSpan.FromHours(6)) });
        var git = await Through(conductor, await StartStaged(conductor));
        var id = git.Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);
        await conductor.DecideAsync(id, new ControllerDecision(ControllerDecisionKind.Escalate, ["a person decides"], null), Ct);
        _mandates.CountForProblem("problem-1").Should().Be(2);

        var again = await StartStaged(conductor);

        again.Ok.Should().BeTrue("the loop bound belongs to the run, and a new approval starts a new run");
        _problems.Get("problem-1")!.State.Should().Be(ProblemState.InProgress);
    }

    [Fact]
    public async Task The_gateway_reads_the_dossier_of_this_run_and_not_the_one_of_an_earlier_run_of_the_problem()
    {
        var conductor = Conductor();
        var first = (await StartStaged(conductor)).Mission!.Id;
        await conductor.StartedAsync(first, Ct);
        await conductor.HandOverAsync(first, DossierFor(first) with { Claim = "run-1" }, Ct);
        var git1 = (await conductor.DecideAsync(first, Grant(), Ct)).Mission!.Id;
        await conductor.StartedAsync(git1, Ct);
        await conductor.HandOverAsync(git1, DossierFor(git1), Ct);
        await conductor.DecideAsync(git1, new ControllerDecision(ControllerDecisionKind.Escalate, ["a person decides"], null), Ct);
        var second = (await StartStaged(conductor)).Mission!.Id;
        await conductor.StartedAsync(second, Ct);
        await conductor.HandOverAsync(second, DossierFor(second) with { Claim = "run-2" }, Ct);
        var git2 = (await conductor.DecideAsync(second, Grant(), Ct)).Mission!.Id;

        (await conductor.AuthoriseActAsync(git2, GatedAct.MergePullRequest, "abc123", Ct, repository: "example/shop", baseCommit: "base-1")).Allowed.Should().BeTrue();

        _facts.Last!.Dossier!.Claim.Should().Be("run-2");
    }

    [Fact]
    public async Task The_source_of_the_facts_is_asked_about_the_mission_its_frame_and_its_repositories()
    {
        var conductor = Conductor();
        var id = (await StartStaged(conductor)).Mission!.Id;
        await conductor.StartedAsync(id, Ct);
        await conductor.HandOverAsync(id, DossierFor(id), Ct);

        await conductor.DecideAsync(id, Grant(), Ct);

        _facts.Last!.MissionId.Should().Be(id);
        _facts.Last.Frame.Repositories.Should().Equal("example/shop");
        _facts.Last.ProblemId.Should().Be("problem-1");
    }
}
