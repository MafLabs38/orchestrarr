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
    private IPowerPolicy _powers = new ConfiguredPowerPolicy([new PowerGrant(PowerGrant.Wildcard, Both)]);
    private RepositoryState _repository = Repository(pullRequests: 2);
    private WorkItemCatalog? _catalog;

    private static Approver Operator => new("alice", IsPerson: true, IsOperator: true, StepUpAt: null);

    private static PullRequestInfo PullRequest(int number, string head, bool draft = false) =>
        new(number, $"Bump lodash {number}", new Uri($"https://github.com/example/app/pull/{number}"), "dependabot[bot]", "dependabot/npm",
            draft, ChecksState.Passing, MergeState.Clean, [], [], Now, head);

    private async Task<WorkItemCatalog> CatalogOf(IPullRequestSource forge)
    {
        var source = Substitute.For<IFleetSource>();
        source.Kind.Returns("test");
        source.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Snapshot([_repository])));
        var catalog = new WorkItemCatalog(source, forge, _powers, _time, new WorkItemCatalogOptions(), NullLogger<WorkItemCatalog>.Instance);
        await catalog.GetAsync(refresh: false, TestContext.Current.CancellationToken);
        await catalog.EnrichmentIdle;
        _catalog = catalog;
        return catalog;
    }

    private async Task<ApprovalService> Service(IApprovalStore? store = null, IAuditSink? audit = null, params PullRequestInfo[] pullRequests)
    {
        _forge.Items = pullRequests.Length == 0 ? [PullRequest(1, HeadA), PullRequest(2, HeadB)] : pullRequests;
        return new ApprovalService(await CatalogOf(_forge), store ?? _store, _killSwitch, audit ?? _audit, _time);
    }

    private Task<ApprovalService> Service(params PullRequestInfo[] pullRequests) => Service(null, null, pullRequests);

    // What the card displayed: the pull requests that are there, except the drafts (which are never approved).
    private IReadOnlyList<SeenPullRequest> Displayed() =>
        [.. _forge.Items.Where(pullRequest => !pullRequest.Draft).Select(pullRequest => new SeenPullRequest(pullRequest.Number, pullRequest.HeadSha))];

    private Task<ApprovalResult> Approve(ApprovalService service, StepKind[]? steps = null, Approver? approver = null, string id = ItemId, IReadOnlyList<SeenPullRequest>? seen = null) =>
        service.ApproveAsync(approver ?? Operator, id, steps ?? Both, seen ?? Displayed(), TestContext.Current.CancellationToken);

    // --- recording

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
    public async Task The_second_factor_of_the_approver_is_kept_with_the_approval()
    {
        var stepUp = Now.AddMinutes(-1);

        var result = await Approve(await Service(), approver: Operator with { StepUpAt = stepUp });

        result.Approvals.Should().OnlyContain(a => a.StepUpAt == stepUp);
    }

    [Fact]
    public async Task A_draft_is_never_approved_and_a_list_of_drafts_gives_nothing_to_approve()
    {
        var result = await Approve(await Service(PullRequest(1, HeadA), PullRequest(2, HeadB, draft: true)));
        result.Approvals.Should().ContainSingle().Which.PullRequest.Should().Be(1);

        var onlyDrafts = await Approve(await Service(PullRequest(3, HeadA, draft: true)));
        onlyDrafts.Refusal.Should().Be(ApprovalRefusal.NoPullRequests);
    }

    [Fact]
    public async Task A_pull_request_whose_commit_is_malformed_is_left_out()
    {
        var service = await Service(PullRequest(1, HeadA), PullRequest(2, "not-a-commit"), PullRequest(3, string.Empty));

        var result = await Approve(service, seen: [new SeenPullRequest(1, HeadA)]);

        result.Approvals.Should().ContainSingle().Which.PullRequest.Should().Be(1);
    }

    [Fact]
    public async Task When_every_commit_is_malformed_there_is_nothing_to_approve()
    {
        var service = await Service(PullRequest(1, "not-a-commit"), PullRequest(2, string.Empty));

        (await Approve(service, seen: [])).Refusal.Should().Be(ApprovalRefusal.NoPullRequests);
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

    [Fact]
    public async Task Ten_approvals_of_the_same_decision_at_once_record_it_once()
    {
        var service = await Service();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Approve(service)));

        results.Count(r => r.Recorded).Should().Be(1);
        results.Where(r => !r.Recorded).Should().OnlyContain(r => r.Refusal == ApprovalRefusal.AlreadyApproved);
        _store.List().Should().HaveCount(2);
        _audit.List().Count(e => e.Event == ApprovalAuditEvents.Recorded).Should().Be(2);
    }

    // --- what the operator saw

    [Fact]
    public async Task What_was_displayed_must_be_exactly_what_is_there_now()
    {
        var service = await Service(PullRequest(1, HeadA), PullRequest(2, HeadB));

        // The bot pushed after the card was drawn, a pull request was opened, or one was closed: the approval would cover what was not seen
        (await Approve(service, seen: [new SeenPullRequest(1, HeadA), new SeenPullRequest(2, new string('c', 40))])).Refusal.Should().Be(ApprovalRefusal.PlanChanged);
        (await Approve(service, seen: [new SeenPullRequest(1, HeadA)])).Refusal.Should().Be(ApprovalRefusal.PlanChanged);
        (await Approve(service, seen: [new SeenPullRequest(1, HeadA), new SeenPullRequest(2, HeadB), new SeenPullRequest(3, HeadA)])).Refusal.Should().Be(ApprovalRefusal.PlanChanged);
        (await Approve(service, seen: [])).Refusal.Should().Be(ApprovalRefusal.PlanChanged);
        _store.List().Should().BeEmpty();
    }

    [Fact]
    public async Task A_displayed_list_with_a_null_in_it_is_a_changed_plan_not_a_crash()
    {
        var service = await Service(PullRequest(1, HeadA));

        (await Approve(service, seen: [new SeenPullRequest(1, HeadA), null!])).Refusal.Should().Be(ApprovalRefusal.PlanChanged);
    }

    [Fact]
    public async Task The_order_in_which_the_pull_requests_were_displayed_does_not_matter()
    {
        var service = await Service(PullRequest(1, HeadA), PullRequest(2, HeadB));

        var result = await Approve(service, seen: [new SeenPullRequest(2, HeadB), new SeenPullRequest(1, HeadA)]);

        result.Recorded.Should().BeTrue();
    }

    [Fact]
    public async Task A_push_between_the_display_and_the_click_is_refused()
    {
        var service = await Service(PullRequest(1, HeadA));
        var displayed = Displayed();
        _forge.Items = [PullRequest(1, HeadB)];
        _time.Advance(TimeSpan.FromSeconds(31));
        await _catalog!.GetAsync(refresh: false, TestContext.Current.CancellationToken);
        await _catalog.EnrichmentIdle;

        var result = await Approve(service, seen: displayed);

        result.Refusal.Should().Be(ApprovalRefusal.PlanChanged);
    }

    // --- who may approve, what may be approved

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
        _audit.List().Should().ContainSingle().Which.Detail.Should().StartWith(nameof(ApprovalRefusal.KillSwitchEngaged));

        _killSwitch.Set(false);
        (await Approve(service)).Recorded.Should().BeTrue();
    }

    [Fact]
    public async Task A_stop_engaged_while_the_approval_is_in_flight_records_nothing_and_traces_no_approval()
    {
        var service = await Service(new StopsWhenAsked(_store, _killSwitch));

        var result = await Approve(service);

        result.Refusal.Should().Be(ApprovalRefusal.KillSwitchEngaged);
        _store.List().Should().BeEmpty();
        _audit.List().Should().NotContain(e => e.Event == ApprovalAuditEvents.Recorded);
    }

    [Fact]
    public async Task An_unknown_work_item_or_one_without_a_repository_is_refused()
    {
        var service = await Service();

        (await Approve(service, id: "repository:example/nothing")).Refusal.Should().Be(ApprovalRefusal.UnknownWorkItem);
        (await Approve(service, id: "image:ghcr.io/example/x:1")).Refusal.Should().Be(ApprovalRefusal.UnknownWorkItem);
    }

    [Fact]
    public async Task A_work_item_with_no_pull_request_left_is_unknown()
    {
        _repository = Repository(pullRequests: 0);

        (await Approve(await Service())).Refusal.Should().Be(ApprovalRefusal.UnknownWorkItem);
    }

    [Fact]
    public async Task Nothing_ticked_and_a_step_that_is_not_in_the_plan_are_refused()
    {
        var service = await Service();

        (await Approve(service, steps: [])).Refusal.Should().Be(ApprovalRefusal.NothingTicked);
        (await Approve(service, steps: [StepKind.Deploy])).Refusal.Should().Be(ApprovalRefusal.StepNotOffered);
    }

    [Fact]
    public async Task A_step_that_is_not_granted_is_refused()
    {
        _powers = new ConfiguredPowerPolicy([new PowerGrant(PowerGrant.Wildcard, [StepKind.Validate])]);
        (await Approve(await Service())).Refusal.Should().Be(ApprovalRefusal.StepNotGranted);

        _powers = new NoPowers();
        (await Approve(await Service(), steps: [StepKind.Validate])).Refusal.Should().Be(ApprovalRefusal.StepNotGranted);
    }

    [Fact]
    public async Task Merging_without_validating_first_is_refused_and_validating_alone_is_allowed()
    {
        var service = await Service();

        (await Approve(service, steps: [StepKind.MergePullRequests])).Refusal.Should().Be(ApprovalRefusal.MergeNeedsValidation);
        (await Approve(service, steps: [StepKind.Validate])).Recorded.Should().BeTrue();
    }

    [Fact]
    public async Task A_step_this_vertical_does_not_run_is_refused_even_when_granted()
    {
        _repository = Repository(dependencyAlerts: new AlertCounts(1, 0, 0, 0));
        _powers = new ConfiguredPowerPolicy([new PowerGrant(PowerGrant.Wildcard, [StepKind.Validate, StepKind.MergeFix])]);

        var result = await Approve(await Service(), steps: [StepKind.Validate, StepKind.MergeFix]);

        result.Refusal.Should().Be(ApprovalRefusal.StepNotSupported);
    }

    [Fact]
    public async Task A_list_that_could_not_be_read_is_refused()
    {
        _forge.Fail = true;
        (await Approve(await Service())).Refusal.Should().Be(ApprovalRefusal.PullRequestsNotRead);
    }

    [Fact]
    public async Task An_old_read_that_could_not_be_renewed_is_refused()
    {
        var service = await Service();
        var displayed = Displayed();
        _forge.Fail = true;
        _time.Advance(TimeSpan.FromMinutes(10));
        await _catalog!.GetAsync(refresh: true, TestContext.Current.CancellationToken);
        await _catalog.EnrichmentIdle;

        var result = await Approve(service, seen: displayed);

        result.Refusal.Should().Be(ApprovalRefusal.PullRequestsNotRead);
    }

    [Fact]
    public async Task Without_a_forge_nothing_can_be_approved()
    {
        var service = new ApprovalService(await CatalogOf(new NoPullRequestSource()), _store, _killSwitch, _audit, _time);

        var result = await Approve(service, seen: [new SeenPullRequest(1, HeadA)]);

        result.Refusal.Should().Be(ApprovalRefusal.PullRequestsNotRead);
    }

    // --- the trail and the store

    [Fact]
    public async Task Without_an_audit_trail_nothing_is_approved()
    {
        var service = await Service(audit: new UnavailableAudit());

        var result = await Approve(service);

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
    public async Task An_approver_whose_name_does_not_look_like_one_is_traced_as_unverified()
    {
        await Approve(await Service(), approver: new Approver("mallory\nforged", IsPerson: true, IsOperator: false, StepUpAt: null));

        _audit.List().Should().ContainSingle().Which.Subject.Should().Be("unverified");
    }

    [Fact]
    public async Task A_trail_that_cannot_hold_them_all_refuses_and_keeps_none_of_them()
    {
        var narrow = new InMemoryAuditLog(capacity: 1);
        var service = await Service(audit: narrow);

        var result = await Approve(service);

        result.Refusal.Should().Be(ApprovalRefusal.AuditUnavailable);
        narrow.List().Should().BeEmpty();
        _store.List().Should().BeEmpty();
    }

    [Fact]
    public async Task The_trail_takes_a_batch_whole_or_not_at_all_and_knows_the_refusals_by_name()
    {
        var log = new InMemoryAuditLog(capacity: 4);
        var token = TestContext.Current.CancellationToken;
        AuditEntry Entry(string name) => new(Now, name, "x", null, null);

        (await log.TryRecordRangeAsync([Entry("a"), Entry("b"), Entry("c")], token)).Should().BeTrue();
        (await log.TryRecordRangeAsync([Entry("d"), Entry("e")], token)).Should().BeFalse();
        log.List().Should().HaveCount(3);
        AuditEvents.IsRefusal(ApprovalAuditEvents.Refused).Should().BeTrue();
        AuditEvents.IsRefusal(AuditEvents.IssueRefused).Should().BeTrue();
        AuditEvents.IsRefusal(AuditEvents.LaunchRefused).Should().BeTrue();
        AuditEvents.IsRefusal(ApprovalAuditEvents.Recorded).Should().BeFalse();
    }

    [Fact]
    public async Task A_queue_that_cannot_hold_them_all_refuses_and_traces_nothing_it_did_not_keep()
    {
        var small = new InMemoryApprovalStore(capacity: 1);
        var service = await Service(small);

        var result = await Approve(service);

        result.Refusal.Should().Be(ApprovalRefusal.StoreFull);
        small.List().Should().BeEmpty();
        _audit.List().Should().NotContain(e => e.Event == ApprovalAuditEvents.Recorded);
    }

    [Fact]
    public void The_store_adds_all_or_none_and_lists_the_newest_first()
    {
        var store = new InMemoryApprovalStore(capacity: 3);
        Approval Make(int number, int minutes) => new(Guid.NewGuid(), ItemId, "example/app", number, HeadA, [StepKind.Validate], "alice", Now.AddMinutes(minutes));

        store.TryAddRange([Make(1, 0), Make(2, 5)]).Should().BeTrue();
        store.TryAddRange([Make(3, 9), Make(4, 9)]).Should().BeFalse();
        store.TryAddRange([Make(1, 7)]).Should().BeFalse();
        store.TryAddRange([Make(5, 3), Make(5, 4)]).Should().BeFalse();

        store.Free.Should().Be(1);
        store.List().Select(a => a.PullRequest).Should().Equal(2, 1);
        store.Contains(Make(2, 99)).Should().BeTrue();
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
    public async Task Refusals_cannot_use_up_the_room_a_decision_or_a_stop_needs()
    {
        var log = new InMemoryAuditLog(capacity: 8);
        var token = TestContext.Current.CancellationToken;
        for (var i = 0; i < 20; i++)
        {
            await log.TryRecordAsync(new AuditEntry(Now, ApprovalAuditEvents.Refused, "x", null, null), token);
        }

        log.List().Should().HaveCount(6);
        (await log.TryRecordAsync(new AuditEntry(Now, ApprovalAuditEvents.Recorded, "alice", null, null), token)).Should().BeTrue();
        (await log.TryRecordAsync(new AuditEntry(Now, ApprovalAuditEvents.KillSwitchEngaged, "alice", null, null), token)).Should().BeTrue();
    }

    // --- the powers

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
        var policy = new ConfiguredPowerPolicy([new PowerGrant(PowerGrant.Wildcard, [StepKind.Validate, StepKind.Deploy])]);

        policy.IsGranted(StepKind.MergePullRequests, "example/app", null).Should().BeFalse();
        policy.IsGranted(StepKind.Deploy, "example/app", "server-1").Should().BeFalse();
        policy.IsGranted(StepKind.Validate, null, null).Should().BeFalse();
        new ConfiguredPowerPolicy([]).IsGranted(StepKind.Validate, "example/app", null).Should().BeFalse();
    }

    [Fact]
    public async Task The_launcher_reads_the_same_grants_as_the_card()
    {
        var policy = new ConfiguredPowerPolicy([new PowerGrant("example/app", [StepKind.Validate])]);
        var powers = new PolicyGrantedPowers(policy);
        var order = new LaunchOrder(Guid.NewGuid(), ItemId, "example/app", HeadA, null, [StepKind.Validate], "alice", null, "launcher", Now, Now.AddMinutes(5));
        var token = TestContext.Current.CancellationToken;

        (await powers.AllowsAsync(order, token)).Should().BeTrue();
        (await powers.AllowsAsync(order with { Steps = [StepKind.Validate, StepKind.MergePullRequests] }, token)).Should().BeFalse();
        (await powers.AllowsAsync(order with { Repository = "example/other" }, token)).Should().BeFalse();
        (await powers.AllowsAsync(order with { Environment = "server-1" }, token)).Should().BeFalse();
        (await powers.AllowsAsync(order with { Steps = [] }, token)).Should().BeFalse();
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

        public ValueTask<bool> TryRecordRangeAsync(IReadOnlyList<AuditEntry> entries, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }

    // A store that engages the global stop the first time it is asked for room: the stop arrives while an approval is in flight
    private sealed class StopsWhenAsked(IApprovalStore inner, InMemoryKillSwitch killSwitch) : IApprovalStore
    {
        public int Free
        {
            get
            {
                killSwitch.Set(true);
                return inner.Free;
            }
        }

        public bool Contains(Approval approval) => inner.Contains(approval);

        public bool TryAddRange(IReadOnlyList<Approval> approvals) => inner.TryAddRange(approvals);

        public IReadOnlyList<Approval> List() => inner.List();
    }
}
