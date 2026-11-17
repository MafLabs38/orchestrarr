using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Missions;
using Orchestrarr.Core.SecondFactor;

namespace Orchestrarr.Storage.Test;

public sealed class SqliteMissionStoresTests : IDisposable
{
    private readonly TempDatabase _file = new();
    private readonly MandateSigner _signer = MandateSigner.Create();

    public void Dispose()
    {
        _signer.Dispose();
        _file.Dispose();
    }

    private static Mission Mission(string id = "m1", string problem = "p1") => new(id, problem, "shop", World.Git, "Merge the change", new MissionFrame(["example/shop"], ForbiddenPathsDefaults.Floor, "Git:shop"), new MissionBudget(20, 3, 50), MissionState.Mandated, "mandate-1", 1);

    private SignedMandate Mandate(string id = "mandate-1", string problem = "p1") => _signer.Sign(new Mandate(
        id, "m1", problem, World.Git, "runner-git", [GatedAct.OpenPullRequest, GatedAct.MergePullRequest], new MissionFrame(["example/shop"], ForbiddenPathsDefaults.Floor, "Git:shop"), new MissionBudget(20, 3, 50),
        new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 13, 13, 0, 0, TimeSpan.Zero), MandateSource.Controller, "nonce-" + id, "approval-1", "shop", "deprecated"));

    [Fact]
    public void A_mission_is_kept_with_everything_across_a_restart_and_a_save_replaces_it()
    {
        new SqliteMissionStore(_file.Open()).Save(Mission());

        var reopened = new SqliteMissionStore(_file.Reopen());
        reopened.Get("m1").Should().BeEquivalentTo(Mission());
        reopened.Save(Mission() with { State = MissionState.Running });

        reopened.Get("m1")!.State.Should().Be(MissionState.Running);
        reopened.Get("nothing").Should().BeNull();
    }

    [Fact]
    public void The_missions_of_a_problem_are_listed_in_the_order_they_were_made_and_only_that_problems()
    {
        var store = new SqliteMissionStore(_file.Open());
        store.Save(Mission("m2"));
        store.Save(Mission("m1"));
        store.Save(Mission("m3", "other"));
        store.Save(Mission("m2") with { State = MissionState.Running });

        store.ForProblem("p1").Select(mission => mission.Id).Should().Equal("m2", "m1");
        store.Recent(2).Select(mission => mission.Id).Should().Equal("m3", "m1");
    }

    [Fact]
    public void A_mandate_keeps_its_signature_and_its_certificate_across_a_restart_and_still_verifies()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        using var authority = new Authority(time);
        var entry = new PlanEntry("p1", "shop", "deprecated", "Replace the call", ["example/shop"], World.Release, new MissionBudget(20, 3, 50), new Validation("alice", null), time.GetUtcNow());
        var approval = authority.For(entry)!;
        var mandate = Mandate().Mandate with { ApprovalId = approval.Certificate.Id };
        new SqliteMandateLedger(_file.Open()).Record(_signer.Sign(mandate, approval));

        var read = new SqliteMandateLedger(_file.Reopen()).Get("mandate-1")!;

        read.Mandate.Acts.Should().BeEquivalentTo([GatedAct.OpenPullRequest, GatedAct.MergePullRequest]);
        read.Approval!.Certificate.Id.Should().Be(approval.Certificate.Id);
        time.Advance(TimeSpan.FromMinutes(5));
        using var runner = new MandateVerifier(_signer.PublicKey(), "runner-git", World.Git, new NoState(), time, authority.Verifier);
        runner.Accept(read).Should().Be(MandateRefusal.None);
    }

    [Fact]
    public void A_mandate_is_counted_per_problem_revoked_once_and_a_second_record_does_not_replace_it()
    {
        var ledger = new SqliteMandateLedger(_file.Open());
        ledger.Record(Mandate("a"));
        ledger.Record(Mandate("b"));
        ledger.Record(Mandate("c", "other"));
        ledger.Record(Mandate("a") with { Signature = [1, 2, 3] });

        ledger.CountForProblem("p1").Should().Be(2);
        ledger.IsRevoked("a").Should().BeFalse();
        ledger.Revoke("a");
        ledger.IsRevoked("a").Should().BeTrue();
        ledger.IsRevoked("b").Should().BeFalse();
        ledger.Get("a")!.Signature.Length.Should().BeGreaterThan(3, "the first record stays");
        ledger.IsRevoked("unknown").Should().BeFalse();
    }

    [Fact]
    public void Mandates_are_counted_per_certificate_and_a_revoke_of_an_unknown_mandate_changes_nothing()
    {
        var ledger = new SqliteMandateLedger(_file.Open());
        ledger.Record(Mandate("a"));
        ledger.Record(Mandate("b", "other"));
        ledger.Record(_signer.Sign(Mandate("c").Mandate with { ApprovalId = "approval-2" }));

        ledger.CountForApproval("approval-1").Should().Be(2);
        ledger.CountForApproval("approval-2").Should().Be(1);
        ledger.CountForApproval("none").Should().Be(0);
    }

    [Fact]
    public void Only_the_problems_in_progress_are_listed_and_a_change_of_state_moves_a_problem_out_of_the_list()
    {
        using var authority = new Authority(new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero)));
        var store = new SqliteProblemStore(_file.Open());
        ProblemRun Run(string id, ProblemState state)
        {
            var entry = new PlanEntry(id, "shop", "deprecated", "Replace", ["example/shop"], World.Release, new MissionBudget(20, 3, 50), new Validation("alice", null), new DateTimeOffset(2026, 11, 13, 11, 0, 0, TimeSpan.Zero));
            return new ProblemRun(id, "shop", state, new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero), entry, World.Git, true, authority.For(entry)!);
        }
        store.Save(Run("a", ProblemState.InProgress));
        store.Save(Run("b", ProblemState.Closed));
        store.Save(Run("c", ProblemState.Failed));
        store.Save(Run("d", ProblemState.Escalated));
        store.Save(Run("e", ProblemState.InProgress));

        store.InProgress().Select(run => run.ProblemId).Should().BeEquivalentTo("a", "e");

        store.Save(Run("a", ProblemState.Failed));

        store.InProgress().Select(run => run.ProblemId).Should().Equal("e");
    }

    [Fact]
    public void A_revocation_and_a_problem_that_failed_stay_so_after_a_restart()
    {
        using var authority = new Authority(new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero)));
        var entry = new PlanEntry("p1", "shop", "deprecated", "Replace", ["example/shop"], World.Release, new MissionBudget(20, 3, 50), new Validation("alice", null), new DateTimeOffset(2026, 11, 13, 11, 0, 0, TimeSpan.Zero));
        var database = _file.Open();
        var ledger = new SqliteMandateLedger(database);
        ledger.Record(Mandate("a"));
        ledger.Revoke("a");
        var problems = new SqliteProblemStore(database);
        var run = new ProblemRun("p1", "shop", ProblemState.InProgress, new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero), entry, World.Git, true, authority.For(entry)!);
        problems.Save(run);
        problems.Save(run with { State = ProblemState.Failed });

        var reopened = _file.Reopen();

        new SqliteMandateLedger(reopened).IsRevoked("a").Should().BeTrue("a withdrawn power stays withdrawn after a restart");
        new SqliteMandateLedger(reopened).CountForApproval("approval-1").Should().Be(1, "the count that bounds a certificate survives a restart");
        new SqliteMandateLedger(reopened).CountForRun("p1", "approval-1").Should().Be(1);
        new SqliteMandateLedger(reopened).CountForRun("p1", "approval-2").Should().Be(0);
        new SqliteProblemStore(reopened).InProgress().Should().BeEmpty();
    }

    [Fact]
    public void A_stored_document_that_cannot_be_read_is_a_failure_of_the_store_never_a_row_that_is_left_out()
    {
        var database = _file.Open();
        var problems = new SqliteProblemStore(database);
        using (var connection = database.Connect())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO problems (problem_id, state, body) VALUES ('broken', 'InProgress', '{ not json');";
            command.ExecuteNonQuery();
        }

        var inProgress = () => problems.InProgress();
        var get = () => problems.Get("broken");

        inProgress.Should().Throw<Orchestrarr.Core.Approvals.StorageUnavailableException>("the sweep that stops problems must not skip one it cannot read");
        get.Should().Throw<Orchestrarr.Core.Approvals.StorageUnavailableException>();
    }

    [Fact]
    public void A_page_of_mandates_problems_and_dossiers_is_read_in_one_read_each_and_what_does_not_exist_is_left_out()
    {
        var entry = new PlanEntry("p1", "shop", "deprecated", "Replace the call", ["example/shop"], World.Release, new MissionBudget(20, 3, 50), new Validation("alice", null), new DateTimeOffset(2026, 11, 13, 11, 0, 0, TimeSpan.Zero));
        using var authority = new Authority(new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero)));
        var database = _file.Open();
        var mandates = new SqliteMandateLedger(database);
        mandates.Record(Mandate("mandate-1"));
        mandates.Record(Mandate("mandate-2"));
        mandates.Revoke("mandate-2");
        new SqliteProblemStore(database).Save(new ProblemRun("p1", "shop", ProblemState.InProgress, new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero), entry, World.Git, UsesStaging: true, authority.For(entry)!, World.Staging));
        new SqliteDossierStore(database).Save(new Dossier("m1", "done", "replaced the call", ["sha256:aa"], [], false, null));

        mandates.GetMany(["mandate-1", "mandate-2", "none"]).Keys.Should().BeEquivalentTo("mandate-1", "mandate-2");
        mandates.RevokedAmong(["mandate-1", "mandate-2", "none"]).Should().BeEquivalentTo("mandate-2");
        new SqliteProblemStore(database).GetMany(["p1", "p1", "none"]).Keys.Should().BeEquivalentTo("p1");
        new SqliteDossierStore(database).GetMany(["m1", "m2"]).Keys.Should().BeEquivalentTo("m1");
        mandates.GetMany([]).Should().BeEmpty();
        mandates.RevokedAmong([]).Should().BeEmpty();
    }

    [Fact]
    public void A_problem_and_a_dossier_are_kept()
    {
        var entry = new PlanEntry("p1", "shop", "deprecated", "Replace the call", ["example/shop"], World.Release, new MissionBudget(20, 3, 50), new Validation("alice", null), new DateTimeOffset(2026, 11, 13, 11, 0, 0, TimeSpan.Zero));
        using var authority = new Authority(new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero)));
        var run = new ProblemRun("p1", "shop", ProblemState.InProgress, new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero), entry, World.Git, UsesStaging: true, authority.For(entry)!, World.Staging);
        var dossier = new Dossier("m1", "done", "replaced the call", ["sha256:aa"], ["check the cache"], true, "a refactor");
        var database = _file.Open();
        new SqliteProblemStore(database).Save(run);
        new SqliteDossierStore(database).Save(dossier);

        var reopened = _file.Reopen();

        new SqliteProblemStore(reopened).Get("p1").Should().BeEquivalentTo(run);
        new SqliteDossierStore(reopened).Get("m1").Should().BeEquivalentTo(dossier);
        new SqliteProblemStore(reopened).Get("none").Should().BeNull();
    }

    [Fact]
    public async Task A_whole_problem_goes_through_the_three_worlds_on_the_file_and_survives_a_restart_in_the_middle()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var ids = 0;
        using var authority = new Authority(time);
        ProblemConductor Conductor(SqliteDatabase database) => new(_signer,
            new MissionStores(new SqliteMissionStore(database), new SqliteMandateLedger(database), new SqliteProblemStore(database), new SqliteDossierStore(database)),
            new PowerSources(authority, authority.Verifier, new Withdrawals(), new Grants(), new NoStop()), new ConductorServices(new GoodFacts(), new Runners(), new Silent(), time, () => $"id-{++ids}"), ConductorOptions.Default);
        var entry = new PlanEntry("p1", "shop", "deprecated", "Replace the call", ["example/shop"], World.Release, new MissionBudget(20, 3, 50), new Validation("alice", null), time.GetUtcNow());
        var first = Conductor(_file.Open());
        var staging = await first.StartAsync(entry, StagingDeclaration.DeclaredStaging, StagingRequirement.BlankMachine, StagingState.NotDefined, TestContext.Current.CancellationToken);
        staging.Refusal.Should().BeNull();
        await first.StartedAsync(staging.Mission!.Id, TestContext.Current.CancellationToken);
        await first.HandOverAsync(staging.Mission.Id, new Dossier(staging.Mission.Id, "done", "changed", [], [], false, null, Commit: "abc123"), TestContext.Current.CancellationToken);

        var second = Conductor(_file.Reopen());
        var git = await second.DecideAsync(staging.Mission.Id, new ControllerDecision(ControllerDecisionKind.Grant, ["good"], null), TestContext.Current.CancellationToken);

        git.Refusal.Should().BeNull(string.Join(",", git.Failures));
        git.Mandate!.Mandate.World.Should().Be(World.Git);
        new SqliteMissionStore(_file.Reopen()).ForProblem("p1").Should().HaveCount(2);
        new SqliteMandateLedger(_file.Reopen()).CountForProblem("p1").Should().Be(2);
    }

    private sealed class NoState : IMandateState
    {
        public bool KillSwitchEngaged => false;

        public MandateUse TryUse(string nonce, string approvalId, int maxMandates) => MandateUse.Used;

        public bool IsApprovalRevoked(string approvalId) => false;

        public bool IsRevoked(string mandateId) => false;
    }

    private sealed class Withdrawals : IApprovalWithdrawals
    {
        public bool IsWithdrawn(string certificateId) => false;
    }

    private sealed class GoodFacts : IFactsSource
    {
        public ValueTask<ObservedFacts> FactsAsync(FactsQuery query, CancellationToken cancellationToken) => ValueTask.FromResult(new ObservedFacts(true, true, true, true, true, true, MergeMayDeploy: false, ExecutedFilesTouched: false, TagCreationBlocked: true, BranchCannotReachWriteCredentials: true, HeadCommit: query.Commit, IdentityConfinedToItsBranches: true, ChangedPaths: ["src/Orders.cs"]));
    }

    private sealed class Grants : IGrantSource
    {
        public World? MaxWorldFor(string applicationId) => World.Release;
    }

    // A real authority and a real second factor: the certificates the stored mandates fall inside are real ones
    private sealed class Authority : IApprovalSource, IDisposable
    {
        private static readonly byte[] Secret = [9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        private readonly TimeProvider _time;
        private readonly ApprovalAuthority _authority;
        private int _operators;

        public Authority(TimeProvider time)
        {
            _time = time;
            _authority = new ApprovalAuthority(new SecondFactorGuard(new Secrets(), new InMemorySecondFactorUseLedger(), time, new SecondFactorOptions()), time, () => Guid.NewGuid().ToString("N"));
            Verifier = new ApprovalVerifier(_authority.PublicKey(), time);
        }

        public ApprovalVerifier Verifier { get; }

        public SignedApproval? For(PlanEntry entry)
        {
            var code = Totp.Code(Secret, Totp.StepAt(_time.GetUtcNow(), new TotpOptions()), new TotpOptions());
            var request = new ApprovalRequest(CertificateKind.Problem, entry.ProblemId, entry.ApplicationId, entry.Repositories, entry.MaxWorld, entry.Budget, TimeSpan.FromHours(2));
            var approver = $"alice-{++_operators}";
            return _authority.Propose(request, approver) is { } proposal ? _authority.ApproveAsync(proposal.Id, approver, code, CancellationToken.None).GetAwaiter().GetResult().Approval : null;
        }

        public void Dispose()
        {
            _authority.Dispose();
            Verifier.Dispose();
        }

        private sealed class Secrets : ISecondFactorSecrets
        {
            public ValueTask<byte[]?> FindAsync(string subject, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(Secret);
        }
    }

    private sealed class NoStop : IStopSignal
    {
        public bool Engaged => false;
    }

    private sealed class Silent : IMissionAudit
    {
        public ValueTask<bool> RecordAsync(string eventName, string subject, string? detail, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class Runners : IRunnerDirectory
    {
        public string RunnerFor(World world, string applicationId) => $"runner-{world}";
    }
}
