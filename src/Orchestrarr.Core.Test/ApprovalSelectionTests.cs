using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Core.WorkItems;
using static Orchestrarr.Core.Test.FleetBuilders;

namespace Orchestrarr.Core.Test;

/// <summary>A selection of the table is approved with one code, as one batch for the runner, whatever the number of applications (ADR 0019).</summary>
public sealed class ApprovalSelectionTests
{
    private const string Shop = "repository:example/shop";
    private const string Web = "repository:example/web";
    private const string Code = "123456";
    private static readonly string HeadA = new('a', 40);
    private static readonly string HeadB = new('b', 40);
    private static readonly string HeadC = new('c', 40);
    private static readonly StepKind[] Qualify = [StepKind.Validate];
    private static readonly StepKind[] Both = [StepKind.Validate, StepKind.MergePullRequests];
    private static readonly TaskOptions Tasks = new();

    private readonly FakeTimeProvider _time = new(Now);
    private readonly InMemoryApprovalStore _store = new();
    private readonly InMemoryKillSwitch _killSwitch = new();
    private readonly InMemoryAuditLog _audit = new();
    private readonly PerRepositoryForge _forge = new();
    private InMemoryBatchQueue _queue = null!;

    private static Approver Operator => new("alice", IsPerson: true, IsOperator: true, StepUpAt: null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PullRequestInfo PullRequest(string repository, int number, string head, bool draft = false) =>
        new(number, $"Bump a {number}", new Uri($"https://github.com/{repository}/pull/{number}"), "dependabot[bot]", "dependabot/npm",
            draft, ChecksState.Passing, MergeState.Clean, [], [], Now, head);

    private Task<ApprovalService> Service(IPowerPolicy? powers = null, ISecondFactorPolicy? policy = null)
    {
        _forge.Set("example/shop", [PullRequest("example/shop", 1, HeadA), PullRequest("example/shop", 2, HeadB)]);
        _forge.Set("example/web", [PullRequest("example/web", 7, HeadC)]);
        return ServiceWithoutReset(null, powers, policy);
    }

    // The forge holds what the test put there
    private async Task<ApprovalService> ServiceWithoutReset(string[]? repositories, IPowerPolicy? powers = null, ISecondFactorPolicy? policy = null)
    {
        var source = Substitute.For<IFleetSource>();
        source.Kind.Returns("test");
        source.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Snapshot([.. (repositories ?? ["example/shop", "example/web"]).Select(name => Repository(name, pullRequests: _forge.Count(name)))])));
        var catalog = new WorkItemCatalog(source, _forge, powers ?? new ConfiguredPowerPolicy([new PowerGrant(PowerGrant.Wildcard, Both)]), _time, new WorkItemCatalogOptions(), NullLogger<WorkItemCatalog>.Instance);
        await catalog.GetAsync(refresh: false, Ct);
        await catalog.EnrichmentIdle;
        _queue = new InMemoryBatchQueue(_store, _audit, _killSwitch, _time, Tasks);
        return new ApprovalService(catalog, _store, _killSwitch, _audit, _time, new InMemoryApprovalLedger(_store, _audit, _killSwitch, _queue), new NullApprovalJournal(), Tasks, policy);
    }

    private static SelectedProblem Problem(string item, int number, string head, StepKind[] steps, string? note = null) => new(item, number, head, steps, note);

    private static Task<ApprovalResult> Approve(ApprovalService service, IReadOnlyList<SelectedProblem> selection, string? code = Code, Approver? approver = null) =>
        service.ApproveSelectionAsync(approver ?? Operator, selection, code, Ct);

    [Fact]
    public async Task A_repair_becomes_one_repair_task_with_no_validation_or_merge_beside_it()
    {
        var service = await Service(new ConfiguredPowerPolicy([new PowerGrant(PowerGrant.Wildcard, [StepKind.Validate, StepKind.MergePullRequests, StepKind.Repair])]));

        var result = await Approve(service, [Problem(Shop, 1, HeadA, [StepKind.Repair], "goesUpTo=Qualification repair=True")]);

        result.Recorded.Should().BeTrue();
        var batch = (await _queue.ClaimAsync("runner-1", Ct)).Delivery!.Batch;
        batch.Tasks.Select(task => task.Kind).Should().Equal(TaskKind.RepairPullRequest);
    }

    [Fact]
    public async Task Problems_of_two_applications_become_one_batch_checked_once_with_the_steps_each_row_stands_for()
    {
        var service = await Service();

        var result = await Approve(service, [Problem(Shop, 1, HeadA, Both, "goesUpTo=Production"), Problem(Shop, 2, HeadB, Qualify), Problem(Web, 7, HeadC, Both)]);

        result.Recorded.Should().BeTrue();
        result.Approvals.Should().HaveCount(3);
        var claim = await _queue.ClaimAsync("runner-1", Ct);
        var batch = claim.Delivery!.Batch;
        batch.WorkItemId.Should().StartWith("selection:", "a selection of several applications is not one work item");
        batch.Tasks.Select(task => (task.Repository, task.PullRequest, task.Kind)).Should().Equal(
            ("example/shop", 1, TaskKind.ValidatePullRequest), ("example/shop", 1, TaskKind.MergePullRequest),
            ("example/shop", 2, TaskKind.ValidatePullRequest),
            ("example/web", 7, TaskKind.ValidatePullRequest), ("example/web", 7, TaskKind.MergePullRequest));
        batch.Tasks.Select(task => task.Sequence).Should().Equal(1, 2, 3, 4, 5);
        claim.Delivery.SecondFactorCode.Should().Be(Code, "one code for the whole selection");
        (await _queue.ClaimAsync("runner-1", Ct)).Status.Should().Be(ClaimStatus.Nothing, "there is one batch, not one per application");
    }

    [Fact]
    public async Task A_selection_of_one_application_is_named_after_it_and_the_trail_keeps_the_effective_plan()
    {
        var service = await Service();

        await Approve(service, [Problem(Shop, 1, HeadA, Both, "goesUpTo=Production stopBeforeProduction=True descriptions=abc123")]);

        (await _queue.ClaimAsync("runner-1", Ct)).Delivery!.Batch.WorkItemId.Should().Be(Shop);
        _audit.List().Should().Contain(entry => entry.Event == AuditEvents.ApprovalRecorded && entry.Detail!.Contains("stopBeforeProduction=True descriptions=abc123", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_code_nothing_is_queued_when_one_is_required()
    {
        var service = await Service();

        var result = await Approve(service, [Problem(Shop, 1, HeadA, Both)], code: null);

        result.Refusal.Should().Be(ApprovalRefusal.SecondFactorRequired);
        _store.List().Should().BeEmpty();
    }

    [Fact]
    public async Task The_policy_decides_whether_the_selection_needs_a_code()
    {
        var service = await Service(policy: new NoCodePolicy());

        (await Approve(service, [Problem(Shop, 1, HeadA, Both)], code: null)).Recorded.Should().BeTrue();
    }

    [Fact]
    public async Task A_pull_request_that_moved_to_another_commit_is_not_what_the_operator_saw()
    {
        var service = await Service();

        (await Approve(service, [Problem(Shop, 1, new string('f', 40), Both)])).Refusal.Should().Be(ApprovalRefusal.PlanChanged);
        (await Approve(service, [Problem(Shop, 99, HeadA, Both)])).Refusal.Should().Be(ApprovalRefusal.PlanChanged, "a pull request that is not there");
        _store.List().Should().BeEmpty();
    }

    [Fact]
    public async Task A_draft_is_never_approved_even_when_selected()
    {
        _forge.Set("example/shop", [PullRequest("example/shop", 1, HeadA, draft: true)]);
        var service = await ServiceWithoutReset(["example/shop"]);

        (await Approve(service, [Problem(Shop, 1, HeadA, Both)])).Refusal.Should().Be(ApprovalRefusal.PlanChanged);
    }

    [Fact]
    public async Task The_same_pull_request_twice_unknown_items_and_an_empty_selection_are_refused()
    {
        var service = await Service();

        (await Approve(service, [Problem(Shop, 1, HeadA, Both), Problem(Shop, 1, HeadA, Qualify)])).Refusal.Should().Be(ApprovalRefusal.PlanChanged);
        (await Approve(service, [Problem("repository:example/other", 1, HeadA, Both)])).Refusal.Should().Be(ApprovalRefusal.UnknownWorkItem);
        (await Approve(service, [])).Refusal.Should().Be(ApprovalRefusal.NoPullRequests);
    }

    [Fact]
    public async Task A_step_that_is_not_granted_is_refused_for_the_whole_selection()
    {
        var service = await Service(powers: new ConfiguredPowerPolicy([new PowerGrant(PowerGrant.Wildcard, Qualify)]));

        var result = await Approve(service, [Problem(Shop, 1, HeadA, Qualify), Problem(Web, 7, HeadC, Both)]);

        result.Refusal.Should().Be(ApprovalRefusal.StepNotGranted);
        _store.List().Should().BeEmpty("all or nothing: the row that could go ahead is not queued alone");
    }

    [Fact]
    public async Task Only_an_operator_who_is_a_person_validates()
    {
        var service = await Service();

        (await Approve(service, [Problem(Shop, 1, HeadA, Both)], approver: Operator with { IsOperator = false })).Refusal.Should().Be(ApprovalRefusal.NotAnOperator);
        (await Approve(service, [Problem(Shop, 1, HeadA, Both)], approver: Operator with { IsPerson = false })).Refusal.Should().Be(ApprovalRefusal.NotAPerson);
    }

    [Fact]
    public async Task The_same_selection_is_not_approved_twice_at_the_same_commit()
    {
        var service = await Service();
        var selection = new[] { Problem(Shop, 1, HeadA, Both) };
        (await Approve(service, selection)).Recorded.Should().BeTrue();

        (await Approve(service, selection)).Refusal.Should().Be(ApprovalRefusal.AlreadyApproved);
    }

    [Fact]
    public async Task A_selection_with_more_repairs_than_one_approval_may_start_is_refused_now()
    {
        var many = Enumerable.Range(1, TaskLimits.MaxRepairsPerBatch + 1).Select(number => PullRequest("example/shop", number, new string((char)('a' + (number % 6)), 40))).ToList();
        _forge.Set("example/shop", many);
        var service = await ServiceWithoutReset(["example/shop"], new ConfiguredPowerPolicy([new PowerGrant(PowerGrant.Wildcard, [StepKind.Validate, StepKind.MergePullRequests, StepKind.Repair])]));

        var result = await Approve(service, [.. many.Select(pullRequest => Problem(Shop, pullRequest.Number, pullRequest.HeadSha, [StepKind.Repair], "goesUpTo=Qualification repair=True"))]);

        result.Refusal.Should().Be(ApprovalRefusal.TooManyRepairs);
    }

    [Fact]
    public async Task A_selection_too_big_for_one_batch_is_refused_now_and_not_in_the_runs_screen_later()
    {
        var many = Enumerable.Range(1, 101).Select(number => PullRequest("example/shop", number, new string((char)('a' + (number % 6)), 40))).ToList();
        _forge.Set("example/shop", many);
        var service = await ServiceWithoutReset(["example/shop"]);

        var result = await Approve(service, [.. many.Select(pullRequest => Problem(Shop, pullRequest.Number, pullRequest.HeadSha, Both))]);

        result.Refusal.Should().Be(ApprovalRefusal.TooManyPullRequests);
    }

    [Fact]
    public async Task The_stop_refuses_the_selection()
    {
        var service = await Service();
        await _killSwitch.ChangeAsync(true, new AuditEntry(Now, AuditEvents.KillSwitchEngaged, "alice", null, null), Ct);

        (await Approve(service, [Problem(Shop, 1, HeadA, Both)])).Refusal.Should().Be(ApprovalRefusal.KillSwitchEngaged);
    }

    private sealed class NoCodePolicy : ISecondFactorPolicy
    {
        public bool Required => false;
    }

    private sealed class PerRepositoryForge : IPullRequestSource
    {
        private readonly Dictionary<string, IReadOnlyList<PullRequestInfo>> _byRepository = new(StringComparer.Ordinal);

        public string Kind => "fake";

        public bool Configured => true;

        public void Set(string repository, IReadOnlyList<PullRequestInfo> items) => _byRepository[repository] = items;

        public int Count(string repository) => _byRepository.TryGetValue(repository, out var items) ? items.Count : 0;

        public Task<PullRequestList> ListDependencyPullRequestsAsync(string repositoryFullName, bool refresh, CancellationToken cancellationToken) =>
            Task.FromResult(new PullRequestList(_byRepository.GetValueOrDefault(repositoryFullName) ?? [], Truncated: false));
    }
}
