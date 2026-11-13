using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Missions;

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

    private static Mission Mission(string id = "m1", string problem = "p1") => new(id, problem, "shop", World.Git, "Merge the change", new MissionFrame(["example/shop"], [".github/**"], "Git:shop"), new MissionBudget(20, 3, 50), MissionState.Mandated, "mandate-1", 1);

    private SignedMandate Mandate(string id = "mandate-1", string problem = "p1") => _signer.Sign(new Mandate(
        id, "m1", problem, World.Git, "runner-git", [GatedAct.OpenPullRequest, GatedAct.MergePullRequest], new MissionFrame(["example/shop"], [".github/**"], "Git:shop"), new MissionBudget(20, 3, 50),
        new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 13, 13, 0, 0, TimeSpan.Zero), MandateSource.Controller, "nonce-" + id));

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
    public void A_mandate_keeps_its_signature_across_a_restart_and_still_verifies()
    {
        new SqliteMandateLedger(_file.Open()).Record(Mandate());

        var read = new SqliteMandateLedger(_file.Reopen()).Get("mandate-1")!;

        read.Mandate.Acts.Should().BeEquivalentTo([GatedAct.OpenPullRequest, GatedAct.MergePullRequest]);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 5, 0, TimeSpan.Zero));
        using var runner = new MandateVerifier(_signer.PublicKey(), "runner-git", World.Git, new NoState(), time);
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
    public void A_problem_and_a_dossier_are_kept()
    {
        var entry = new PlanEntry("p1", "shop", "deprecated", "Replace the call", ["example/shop"], World.Release, new MissionBudget(20, 3, 50), new Validation("alice", null), new DateTimeOffset(2026, 11, 13, 11, 0, 0, TimeSpan.Zero));
        var run = new ProblemRun("p1", "shop", ProblemState.InProgress, new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero), entry, World.Git, UsesStaging: true, World.Staging);
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
    public void A_whole_problem_goes_through_the_three_worlds_on_the_file_and_survives_a_restart_in_the_middle()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var ids = 0;
        ProblemConductor Conductor(SqliteDatabase database) => new(_signer, new SqliteMissionStore(database), new SqliteMandateLedger(database), new SqliteProblemStore(database), new SqliteDossierStore(database),
            new GoodFacts(), new Runners(), new Yes(), new NoStop(), new Silent(), time, ConductorOptions.Default, () => $"id-{++ids}");
        var entry = new PlanEntry("p1", "shop", "deprecated", "Replace the call", ["example/shop"], World.Release, new MissionBudget(20, 3, 50), new Validation("alice", null), time.GetUtcNow());
        var first = Conductor(_file.Open());
        var staging = first.Start(entry, StagingDeclaration.DeclaredStaging, StagingRequirement.BlankMachine, StagingState.NotDefined, World.Release, null);
        staging.Refusal.Should().BeNull();
        first.Started(staging.Mission!.Id);
        first.HandOver(staging.Mission.Id, new Dossier(staging.Mission.Id, "done", "changed", [], [], false, null));

        var second = Conductor(_file.Reopen());
        var git = second.Decide(staging.Mission.Id, new ControllerDecision(ControllerDecisionKind.Grant, ["good"], null));

        git.Refusal.Should().BeNull(string.Join(",", git.Failures));
        git.Mandate!.Mandate.World.Should().Be(World.Git);
        new SqliteMissionStore(_file.Reopen()).ForProblem("p1").Should().HaveCount(2);
        new SqliteMandateLedger(_file.Reopen()).CountForProblem("p1").Should().Be(2);
    }

    private sealed class NoState : IMandateState
    {
        public bool KillSwitchEngaged => false;

        public bool TryUse(string nonce) => true;

        public bool IsRevoked(string mandateId) => false;
    }

    private sealed class GoodFacts : IFactsSource
    {
        public ObservedFacts Facts(string problemId, World world, Dossier? dossier) => new(true, true, true, true, true, true);
    }

    private sealed class Yes : IApprovalCheck
    {
        public bool IsProved(PlanEntry entry) => true;
    }

    private sealed class NoStop : IStopSignal
    {
        public bool Engaged => false;
    }

    private sealed class Silent : IMissionAudit
    {
        public bool Record(string eventName, string subject, string? detail) => true;
    }

    private sealed class Runners : IRunnerDirectory
    {
        public string RunnerFor(World world, string applicationId) => $"runner-{world}";
    }
}
