using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.WorkItems;
using static Orchestrarr.Core.Test.FleetBuilders;

namespace Orchestrarr.Core.Test;

public sealed class ApprovalServiceTests
{
    private const string ItemId = "repository:example/app";
    private static readonly string HeadA = new('a', 40);
    private static readonly string HeadB = new('b', 40);
    private static readonly StepKind[] Both = [StepKind.Validate, StepKind.MergePullRequests];

    private readonly FakeTimeProvider _time = new(Now);
    private readonly InMemoryApprovalStore _store = new();
    private readonly InMemoryKillSwitch _killSwitch = new();
    private readonly InMemoryAuditLog _audit = new();
    private readonly FakeForge _forge = new();
    private IPowerPolicy _powers = new ConfiguredPowerPolicy([new PowerGrant("*", Both)]);
    private RepositoryState _repository = Repository(pullRequests: 2);

    private static Approver Operator => new("alice", IsPerson: true, IsOperator: true, StepUpAt: null);

    private static PullRequestInfo PullRequest(int number, string head, bool draft = false) =>
        new(number, $"Bump lodash {number}", new Uri($"https://github.com/example/app/pull/{number}"), "dependabot[bot]", "dependabot/npm",
            draft, ChecksState.Passing, MergeState.Clean, [], [], Now, head);

    private async Task<ApprovalService> Service(params PullRequestInfo[] pullRequests)
    {
        _forge.Items = pullRequests.Length == 0 ? [PullRequest(1, HeadA), PullRequest(2, HeadB)] : pullRequests;
        var source = Substitute.For<IFleetSource>();
        source.Kind.Returns("test");
        source.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Snapshot([_repository])));
        var catalog = new WorkItemCatalog(source, _forge, _powers, _time, new WorkItemCatalogOptions(), NullLogger<WorkItemCatalog>.Instance);
        await catalog.GetAsync(refresh: false, TestContext.Current.CancellationToken);
        await catalog.EnrichmentIdle;
        return new ApprovalService(catalog, _store, _killSwitch, _audit, _time);
    }

    private static Task<ApprovalResult> Approve(ApprovalService service, StepKind[]? steps = null, Approver? approver = null, string id = ItemId) =>
        service.ApproveAsync(approver ?? Operator, id, steps ?? Both, TestContext.Current.CancellationToken);

    [Fact]
    public async Task An_approval_is_recorded_per_pull_request_bound_to_the_commit_the_server_read()
    {
        var result = await Approve(await Service());

        result.Recorded.Should().BeTrue();
        result.Approvals.Select(a => (a.PullRequest, a.HeadSha)).Should().BeEquivalentTo([(1, HeadA), (2, HeadB)]);
        result.Approvals.Should().OnlyContain(a => a.ApproverSubject == "alice" && a.Repository == "example/app" && a.ApprovedAt == Now);
        _store.List().Should().HaveCount(2);
    }

    [Fact]
    public async Task A_draft_pull_request_is_not_approved_and_a_list_of_drafts_gives_nothing_to_approve()
    {
        var result = await Approve(await Service(PullRequest(1, HeadA), PullRequest(2, HeadB, draft: true)));
        result.Approvals.Should().ContainSingle().Which.PullRequest.Should().Be(1);

        var onlyDrafts = await Approve(await Service(PullRequest(3, HeadA, draft: true)));
        onlyDrafts.Refusal.Should().Be(ApprovalRefusal.NoPullRequests);
    }

    [Fact]
    public async Task A_pull_request_whose_commit_is_malformed_is_left_out()
    {
        var result = await Approve(await Service(PullRequest(1, HeadA), PullRequest(2, "not-a-commit"), PullRequest(3, string.Empty)));

        result.Approvals.Should().ContainSingle().Which.PullRequest.Should().Be(1);
    }

    [Fact]
    public async Task The_same_decision_cannot_be_approved_twice_but_a_new_commit_can()
    {
        var first = await Approve(await Service(PullRequest(1, HeadA)));
        var again = await Approve(await Service(PullRequest(1, HeadA)));
        var moved = await Approve(await Service(PullRequest(1, HeadB)));

        first.Recorded.Should().BeTrue();
        again.Refusal.Should().Be(ApprovalRefusal.AlreadyApproved);
        moved.Recorded.Should().BeTrue();
        _store.List().Should().HaveCount(2);
    }

    [Fact]
    public async Task Only_the_new_pull_requests_are_recorded_when_some_were_approved_before()
    {
        await Approve(await Service(PullRequest(1, HeadA)));

        var result = await Approve(await Service(PullRequest(1, HeadA), PullRequest(2, HeadB)));

        result.Approvals.Should().ContainSingle().Which.PullRequest.Should().Be(2);
    }

    [Theory]
    [InlineData(false, true, ApprovalRefusal.NotAPerson)]
    [InlineData(true, false, ApprovalRefusal.NotAnOperator)]
    public async Task Only_a_person_with_the_operator_role_can_approve(bool person, bool isOperator, ApprovalRefusal expected)
    {
        var result = await Approve(await Service(), approver: new Approver("svc", person, isOperator, null));

        result.Refusal.Should().Be(expected);
        _store.List().Should().BeEmpty();
    }

    [Fact]
    public async Task The_kill_switch_stops_approving()
    {
        var service = await Service();
        _killSwitch.Set(true);

        (await Approve(service)).Refusal.Should().Be(ApprovalRefusal.KillSwitchEngaged);
        _store.List().Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_work_item_or_one_without_a_repository_is_refused()
    {
        var service = await Service();

        (await Approve(service, id: "repository:example/nothing")).Refusal.Should().Be(ApprovalRefusal.UnknownWorkItem);
        (await Approve(service, id: "image:ghcr.io/example/x:1")).Refusal.Should().Be(ApprovalRefusal.UnknownWorkItem);
    }

    [Fact]
    public async Task Nothing_ticked_a_step_that_is_not_in_the_plan_and_a_step_not_granted_are_refused()
    {
        var service = await Service();
        (await Approve(service, steps: [])).Refusal.Should().Be(ApprovalRefusal.NothingTicked);
        (await Approve(service, steps: [StepKind.Deploy])).Refusal.Should().Be(ApprovalRefusal.StepNotOffered);

        _powers = new ConfiguredPowerPolicy([new PowerGrant("*", [StepKind.Validate])]);
        (await Approve(await Service())).Refusal.Should().Be(ApprovalRefusal.StepNotGranted);

        _powers = new NoPowers();
        (await Approve(await Service(), steps: [StepKind.Validate])).Refusal.Should().Be(ApprovalRefusal.StepNotGranted);
    }

    [Fact]
    public async Task Merging_without_validating_first_is_refused()
    {
        (await Approve(await Service(), steps: [StepKind.MergePullRequests])).Refusal.Should().Be(ApprovalRefusal.MergeNeedsValidation);
    }

    [Fact]
    public async Task Validating_alone_is_allowed()
    {
        (await Approve(await Service(), steps: [StepKind.Validate])).Recorded.Should().BeTrue();
    }

    [Fact]
    public async Task A_step_this_vertical_does_not_run_is_refused_even_when_granted()
    {
        _repository = Repository(dependencyAlerts: new AlertCounts(1, 0, 0, 0));
        _powers = new ConfiguredPowerPolicy([new PowerGrant("*", [StepKind.Validate, StepKind.MergeFix])]);

        var result = await Approve(await Service(), steps: [StepKind.Validate, StepKind.MergeFix]);

        result.Refusal.Should().Be(ApprovalRefusal.StepNotSupported);
    }

    [Fact]
    public async Task What_the_operator_sees_is_what_is_approved_a_list_not_read_or_an_old_read_is_refused()
    {
        _forge.Fail = true;
        (await Approve(await Service())).Refusal.Should().Be(ApprovalRefusal.PullRequestsNotRead);

        _forge.Fail = false;
        _repository = Repository(pullRequests: 0);
        (await Approve(await Service())).Refusal.Should().Be(ApprovalRefusal.UnknownWorkItem);
    }

    [Fact]
    public async Task Without_a_forge_nothing_can_be_approved()
    {
        var source = Substitute.For<IFleetSource>();
        source.Kind.Returns("test");
        source.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Snapshot([_repository])));
        var catalog = new WorkItemCatalog(source, new NoPullRequestSource(), _powers, _time, new WorkItemCatalogOptions(), NullLogger<WorkItemCatalog>.Instance);
        await catalog.GetAsync(refresh: false, TestContext.Current.CancellationToken);
        await catalog.EnrichmentIdle;

        var result = await new ApprovalService(catalog, _store, _killSwitch, _audit, _time).ApproveAsync(Operator, ItemId, Both, TestContext.Current.CancellationToken);

        result.Refusal.Should().Be(ApprovalRefusal.PullRequestsNotRead);
    }

    [Fact]
    public async Task Without_an_audit_trail_nothing_is_approved()
    {
        var service = new ApprovalService(await CatalogOf(), _store, _killSwitch, new UnavailableAudit(), _time);

        var result = await service.ApproveAsync(Operator, ItemId, Both, TestContext.Current.CancellationToken);

        result.Refusal.Should().Be(ApprovalRefusal.AuditUnavailable);
        _store.List().Should().BeEmpty();
    }

    [Fact]
    public async Task Every_approval_leaves_who_approved_what_and_every_refusal_leaves_its_code()
    {
        var service = await Service(PullRequest(1, HeadA));
        await Approve(service);
        await Approve(service, steps: []);

        var entries = _audit.List().Reverse().ToList();
        entries[0].Event.Should().Be(ApprovalAuditEvents.Recorded);
        entries[0].Subject.Should().Be("alice");
        entries[0].Detail.Should().Contain("example/app#1").And.Contain(HeadA).And.Contain("Validate");
        entries[1].Event.Should().Be(ApprovalAuditEvents.Refused);
        entries[1].Detail.Should().StartWith(nameof(ApprovalRefusal.NothingTicked));
    }

    [Fact]
    public async Task A_work_item_id_that_does_not_look_like_one_never_reaches_the_trail()
    {
        await Approve(await Service(), id: "repository:evil\nforged line");

        _audit.List().Should().ContainSingle().Which.Detail.Should().Be(nameof(ApprovalRefusal.UnknownWorkItem));
    }

    [Fact]
    public async Task A_full_store_refuses_instead_of_forgetting()
    {
        var small = new InMemoryApprovalStore(capacity: 1);
        var service = new ApprovalService(await CatalogOf(), small, _killSwitch, _audit, _time);

        var result = await service.ApproveAsync(Operator, ItemId, Both, TestContext.Current.CancellationToken);

        result.Approvals.Should().ContainSingle();
        small.List().Should().ContainSingle();
        (await service.ApproveAsync(Operator, ItemId, [StepKind.Validate], TestContext.Current.CancellationToken)).Refusal.Should().Be(ApprovalRefusal.StoreFull);
    }

    [Fact]
    public async Task The_trail_refuses_past_its_capacity_instead_of_dropping_entries()
    {
        var tiny = new InMemoryAuditLog(capacity: 1);

        (await tiny.TryRecordAsync(new AuditEntry(Now, "a", "x", null, null), TestContext.Current.CancellationToken)).Should().BeTrue();
        (await tiny.TryRecordAsync(new AuditEntry(Now, "b", "x", null, null), TestContext.Current.CancellationToken)).Should().BeFalse();
        tiny.List().Should().ContainSingle();
    }

    [Fact]
    public async Task Approving_never_runs_anything_it_only_records()
    {
        // The service has no launcher, no signer and no runner to call: what it can do is fully described by the store and the trail.
        var service = await Service(PullRequest(1, HeadA));
        await Approve(service);

        typeof(ApprovalService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType)
            .Should().NotContain([typeof(IOrderSigner), typeof(IOrderVerifier), typeof(Launch.Launcher)]);
    }

    private async Task<WorkItemCatalog> CatalogOf()
    {
        _forge.Items = [PullRequest(1, HeadA), PullRequest(2, HeadB)];
        var source = Substitute.For<IFleetSource>();
        source.Kind.Returns("test");
        source.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Snapshot([_repository])));
        var catalog = new WorkItemCatalog(source, _forge, _powers, _time, new WorkItemCatalogOptions(), NullLogger<WorkItemCatalog>.Instance);
        await catalog.GetAsync(refresh: false, TestContext.Current.CancellationToken);
        await catalog.EnrichmentIdle;
        return catalog;
    }

    [Theory]
    [InlineData("*", "example/app", true)]
    [InlineData("example/app", "example/app", true)]
    [InlineData("Example/App", "example/app", true)]
    [InlineData("example/other", "example/app", false)]
    public void A_grant_covers_a_repository_by_name_or_all_of_them(string grantRepository, string repository, bool expected)
    {
        new ConfiguredPowerPolicy([new PowerGrant(grantRepository, [StepKind.Validate])]).IsGranted(StepKind.Validate, repository, null).Should().Be(expected);
    }

    [Fact]
    public void A_grant_covers_only_the_steps_it_lists_and_never_a_step_on_an_environment()
    {
        var policy = new ConfiguredPowerPolicy([new PowerGrant("*", [StepKind.Validate, StepKind.Deploy])]);

        policy.IsGranted(StepKind.MergePullRequests, "example/app", null).Should().BeFalse();
        policy.IsGranted(StepKind.Deploy, "example/app", "server-1").Should().BeFalse();
        policy.IsGranted(StepKind.Validate, null, null).Should().BeFalse();
        new ConfiguredPowerPolicy([]).IsGranted(StepKind.Validate, "example/app", null).Should().BeFalse();
    }

    private sealed class FakeForge : IPullRequestSource
    {
        public IReadOnlyList<PullRequestInfo> Items { get; set; } = [];

        public bool Fail { get; set; }

        public string Kind => "fake";

        public bool Configured => true;

        public Task<PullRequestList> ListDependencyPullRequestsAsync(string repositoryFullName, bool refresh, CancellationToken cancellationToken) =>
            Fail ? Task.FromException<PullRequestList>(new HttpRequestException("down")) : Task.FromResult(new PullRequestList(Items, Truncated: false));
    }

    private sealed class UnavailableAudit : IAuditSink
    {
        public ValueTask<bool> TryRecordAsync(AuditEntry entry, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }
}
