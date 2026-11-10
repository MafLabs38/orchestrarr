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

public sealed class ApprovalServiceTests
{
    private const string ItemId = "repository:example/app";
    private static readonly string HeadA = new('a', 40);
    private static readonly string HeadB = new('b', 40);
    private static readonly StepKind[] Both = [StepKind.Validate, StepKind.MergePullRequests];

    private readonly FakeTimeProvider _time = new(Now);
    private readonly InMemoryApprovalStore _store = new();
    private const string Code = "123456";
    private readonly InMemoryKillSwitch _killSwitch = new();
    private readonly InMemoryAuditLog _audit = new();
    private static readonly TaskOptions Tasks = new();
    private readonly NullApprovalJournal _journal = new();
    private InMemoryBatchQueue _queue = null!;
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
        var chosenStore = store ?? _store;
        var chosenAudit = audit ?? _audit;
        _queue = new InMemoryBatchQueue(_store, chosenAudit, _killSwitch, _time, Tasks);
        return new ApprovalService(await CatalogOf(_forge), chosenStore, _killSwitch, chosenAudit, _time, new InMemoryApprovalLedger(_store, chosenAudit, _killSwitch, _queue), _journal, Tasks);
    }

    private Task<ApprovalService> Service(params PullRequestInfo[] pullRequests) => Service(null, null, pullRequests);

    private async Task<ApprovalService> ServiceWith(IApprovalLedger ledger, IApprovalStore? store = null)
    {
        _forge.Items = [PullRequest(1, HeadA), PullRequest(2, HeadB)];
        return new ApprovalService(await CatalogOf(_forge), store ?? _store, _killSwitch, _audit, _time, ledger, _journal, Tasks);
    }

    // What the card displayed: the pull requests that are there, except the drafts (which are never approved).
    private IReadOnlyList<SeenPullRequest> Displayed() =>
        [.. _forge.Items.Where(pullRequest => !pullRequest.Draft).Select(pullRequest => new SeenPullRequest(pullRequest.Number, pullRequest.HeadSha))];

    private Task<ApprovalResult> Approve(ApprovalService service, StepKind[]? steps = null, Approver? approver = null, string id = ItemId, IReadOnlyList<SeenPullRequest>? seen = null) =>
        service.ApproveAsync(approver ?? Operator, id, steps ?? Both, seen ?? Displayed(), Code, TestContext.Current.CancellationToken);

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
    public async Task A_card_with_more_pull_requests_than_a_batch_can_carry_is_refused_at_once()
    {
        var pullRequests = Enumerable.Range(1, 101).Select(number => PullRequest(number, HeadA)).ToArray();
        var seen = pullRequests.Select(pullRequest => new SeenPullRequest(pullRequest.Number, HeadA)).ToArray();

        var result = await Approve(await Service(pullRequests), seen: seen);

        result.Refusal.Should().Be(ApprovalRefusal.TooManyPullRequests, "two tasks each makes more than the executor accepts in one batch");
        _store.List().Should().BeEmpty();
    }

    [Fact]
    public async Task The_second_batch_holds_only_the_tasks_of_the_new_pull_requests()
    {
        await Approve(await Service(PullRequest(1, HeadA)));

        var result = await Approve(await Service(PullRequest(1, HeadA), PullRequest(2, HeadB)));

        var second = _queue.Runs(10).Single(batch => batch.BatchId == result.Approvals.Single().BatchId);
        var tasks = _queue.Find(second.BatchId)!.Tasks;
        tasks.Select(task => task.Spec.PullRequest).Should().OnlyContain(number => number == 2, "a pull request already in an open approval is not done twice");
        tasks.Select(task => task.Spec.ApprovalId).Distinct().Should().BeEquivalentTo(result.Approvals.Select(approval => approval.Id));
    }

    [Fact]
    public async Task Ten_approvals_of_the_same_decision_at_once_record_it_once()
    {
        var service = await Service();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Approve(service)));

        results.Count(r => r.Recorded).Should().Be(1);
        results.Where(r => !r.Recorded).Should().OnlyContain(r => r.Refusal == ApprovalRefusal.AlreadyApproved);
        _store.List().Should().HaveCount(2);
        _audit.List().Count(e => e.Event == AuditEvents.ApprovalRecorded).Should().Be(2);
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
    public async Task The_same_pull_request_twice_in_the_list_is_a_list_to_read_again_and_traces_nothing()
    {
        var service = await Service(PullRequest(1, HeadA), PullRequest(1, HeadA));

        var result = await Approve(service, seen: [new SeenPullRequest(1, HeadA), new SeenPullRequest(1, HeadA)]);

        result.Refusal.Should().Be(ApprovalRefusal.PlanChanged);
        _audit.List().Should().NotContain(e => e.Event == AuditEvents.ApprovalRecorded);
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
        await ChangeStop(_killSwitch, engaged: true);

        (await Approve(service)).Refusal.Should().Be(ApprovalRefusal.KillSwitchEngaged);
        _store.List().Should().BeEmpty();
        _audit.List().Should().ContainSingle().Which.Detail.Should().StartWith(nameof(ApprovalRefusal.KillSwitchEngaged));

        await ChangeStop(_killSwitch, engaged: false);
        (await Approve(service)).Recorded.Should().BeTrue();
    }

    [Fact]
    public async Task A_stop_engaged_while_the_approval_is_in_flight_records_nothing_and_traces_no_approval()
    {
        var service = await Service(new StopsWhenAsked(_store, _killSwitch));

        var result = await Approve(service);

        result.Refusal.Should().Be(ApprovalRefusal.KillSwitchEngaged);
        _store.List().Should().BeEmpty();
        _audit.List().Should().NotContain(e => e.Event == AuditEvents.ApprovalRecorded);
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
        var service = new ApprovalService(await CatalogOf(new NoPullRequestSource()), _store, _killSwitch, _audit, _time, new InMemoryApprovalLedger(_store, _audit, _killSwitch), _journal, Tasks);

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
        entries[0].Event.Should().Be(AuditEvents.ApprovalRecorded);
        entries[0].Subject.Should().Be("alice");
        entries[0].Detail.Should().Contain("example/app#1").And.Contain(HeadA).And.Contain("Validate");
        entries[1].Event.Should().Be(AuditEvents.BatchQueued);
        entries[1].Subject.Should().Be("alice");
        entries[2].Event.Should().Be(AuditEvents.ApprovalRefused);
        entries[2].Detail.Should().StartWith(nameof(ApprovalRefusal.NothingTicked));
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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_queue_and_a_trail_cannot_be_built_with_no_room_or_less(int capacity)
    {
        var queue = () => new InMemoryApprovalStore(capacity);
        var trail = () => new InMemoryAuditLog(-1);

        queue.Should().Throw<ArgumentOutOfRangeException>();
        trail.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task A_huge_capacity_does_not_overflow_the_share_of_the_refusals()
    {
        var log = new InMemoryAuditLog(int.MaxValue);

        (await log.TryRecordAsync(new AuditEntry(Now, AuditEvents.ApprovalRefused, "x", null, null), TestContext.Current.CancellationToken)).Should().BeTrue();
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
        AuditEvents.IsRefusal(AuditEvents.ApprovalRefused).Should().BeTrue();
        AuditEvents.IsRefusal(AuditEvents.BatchRefused).Should().BeFalse("the executor's refusals are decisions of an authenticated component");
        AuditEvents.IsRefusal(AuditEvents.ApprovalRecorded).Should().BeFalse();
    }

    [Fact]
    public async Task A_queue_that_cannot_hold_them_all_refuses_and_traces_nothing_it_did_not_keep()
    {
        var small = new InMemoryApprovalStore(capacity: 1);
        var service = await Service(small);

        var result = await Approve(service);

        result.Refusal.Should().Be(ApprovalRefusal.StoreFull);
        small.List().Should().BeEmpty();
        _audit.List().Should().NotContain(e => e.Event == AuditEvents.ApprovalRecorded);
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

        (await tiny.TryRecordAsync(new AuditEntry(Now, AuditEvents.KillSwitchEngaged, "x", null, null), TestContext.Current.CancellationToken)).Should().BeTrue();
        (await tiny.TryRecordAsync(new AuditEntry(Now, AuditEvents.KillSwitchReleased, "x", null, null), TestContext.Current.CancellationToken)).Should().BeFalse();
        tiny.List().Should().ContainSingle();
    }

    [Fact]
    public async Task Refusals_cannot_use_up_the_room_a_decision_or_a_stop_needs()
    {
        var log = new InMemoryAuditLog(capacity: 8);
        var token = TestContext.Current.CancellationToken;
        for (var i = 0; i < 20; i++)
        {
            await log.TryRecordAsync(new AuditEntry(Now, AuditEvents.ApprovalRefused, "x", null, null), token);
        }

        log.List().Should().HaveCount(6);
        (await log.TryRecordAsync(new AuditEntry(Now, AuditEvents.ApprovalRecorded, "alice", null, null), token)).Should().BeTrue();
        (await log.TryRecordAsync(new AuditEntry(Now, AuditEvents.KillSwitchEngaged, "alice", null, null), token)).Should().BeTrue();
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

    private static ValueTask<KillSwitchChange> ChangeStop(InMemoryKillSwitch killSwitch, bool engaged) =>
        killSwitch.ChangeAsync(engaged, new AuditEntry(Now, engaged ? AuditEvents.KillSwitchEngaged : AuditEvents.KillSwitchReleased, "alice-1", null, null), TestContext.Current.CancellationToken);

    // A store that engages the global stop the first time it is asked for room: the stop arrives while an approval is in flight
    private sealed class StopsWhenAsked(IApprovalStore inner, InMemoryKillSwitch killSwitch) : IApprovalStore
    {
        public int Free
        {
            get
            {
                ChangeStop(killSwitch, engaged: true).AsTask().GetAwaiter().GetResult();
                return inner.Free;
            }
        }

        public bool Contains(Approval approval) => inner.Contains(approval);

        public IReadOnlyList<Approval> List(int limit = int.MaxValue) => inner.List(limit);
    }

    // --- where the approvals and their trace are written together

    [Theory]
    [InlineData(LedgerResult.AlreadyThere, ApprovalRefusal.AlreadyApproved)]
    [InlineData(LedgerResult.StoreFull, ApprovalRefusal.StoreFull)]
    [InlineData(LedgerResult.Stopped, ApprovalRefusal.KillSwitchEngaged)]
    [InlineData(LedgerResult.Unavailable, ApprovalRefusal.AuditUnavailable)]
    public async Task What_the_ledger_cannot_record_is_a_refusal_of_the_same_kind(LedgerResult answer, ApprovalRefusal expected)
    {
        var ledger = Substitute.For<IApprovalLedger>();
        ledger.TryRecordAsync(Arg.Any<IReadOnlyList<Approval>>(), Arg.Any<TaskBatch?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<AuditEntry>>(), Arg.Any<CancellationToken>()).Returns(answer);

        var result = await Approve(await ServiceWith(ledger));

        result.Recorded.Should().BeFalse();
        result.Refusal.Should().Be(expected);
    }

    [Fact]
    public async Task The_approvals_and_their_trace_go_to_the_ledger_together_and_the_trace_names_each_approval()
    {
        var ledger = Substitute.For<IApprovalLedger>();
        ledger.TryRecordAsync(Arg.Any<IReadOnlyList<Approval>>(), Arg.Any<TaskBatch?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<AuditEntry>>(), Arg.Any<CancellationToken>()).Returns(LedgerResult.Recorded);

        var result = await Approve(await ServiceWith(ledger));

        result.Recorded.Should().BeTrue();
        await ledger.Received(1).TryRecordAsync(
            Arg.Is<IReadOnlyList<Approval>>(approvals => approvals.Count == 2),
            Arg.Is<TaskBatch?>(batch => batch != null && batch.Tasks.Count == 4),
            Code,
            Arg.Is<IReadOnlyList<AuditEntry>>(entries => entries.Count == 3 && entries.Count(entry => entry.Event == AuditEvents.ApprovalRecorded) == 2 && entries.Count(entry => entry.Event == AuditEvents.BatchQueued) == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_store_that_cannot_be_used_is_a_refusal_and_never_a_trace_of_something_that_was_not_kept()
    {
        var store = Substitute.For<IApprovalStore>();
        store.Contains(Arg.Any<Approval>()).Returns(_ => throw new StorageUnavailableException());
        var ledger = Substitute.For<IApprovalLedger>();

        var result = await Approve(await ServiceWith(ledger, store));

        result.Refusal.Should().Be(ApprovalRefusal.AuditUnavailable);
        await ledger.DidNotReceiveWithAnyArgs().TryRecordAsync(default!, default, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_trail_that_stops_working_while_recording_is_a_refusal_for_the_in_memory_ledger_too()
    {
        var sink = Substitute.For<IAuditSink>();
        sink.TryRecordRangeAsync(Arg.Any<IReadOnlyList<AuditEntry>>(), Arg.Any<CancellationToken>()).Returns<ValueTask<bool>>(_ => throw new StorageUnavailableException());
        var ledger = new InMemoryApprovalLedger(_store, sink, _killSwitch);

        var approval = new Approval(Guid.NewGuid(), ItemId, "example/app", 1, HeadA, [StepKind.Validate], "alice", Now);
        var result = await ledger.TryRecordAsync([approval], [new AuditEntry(Now, AuditEvents.ApprovalRecorded, "alice-1", null, null)], TestContext.Current.CancellationToken);

        result.Should().Be(LedgerResult.Unavailable);
        _store.List().Should().BeEmpty("no trace, no approval");
    }

    [Fact]
    public async Task The_in_memory_ledger_reads_the_stop_at_the_moment_of_writing()
    {
        using var stop = new InMemoryKillSwitch(startEngaged: true);
        var ledger = new InMemoryApprovalLedger(_store, _audit, stop);
        var approval = new Approval(Guid.NewGuid(), ItemId, "example/app", 1, HeadA, [StepKind.Validate], "alice", Now);

        var result = await ledger.TryRecordAsync([approval], [new AuditEntry(Now, AuditEvents.ApprovalRecorded, "alice", null, null)], TestContext.Current.CancellationToken);

        result.Should().Be(LedgerResult.Stopped);
        _store.List().Should().BeEmpty();
        _audit.List().Should().BeEmpty();
    }

    [Fact]
    public void Architecture_guard_the_approval_store_has_no_method_that_takes_approvals_so_that_nothing_is_approved_without_its_trace()
    {
        var writes = typeof(IApprovalStore).GetMethods()
            .Where(method => method.Name != nameof(IApprovalStore.Contains) && method.GetParameters().Any(parameter => parameter.ParameterType.IsAssignableTo(typeof(IEnumerable<Approval>)) || parameter.ParameterType == typeof(Approval)))
            .Select(method => method.Name);

        writes.Should().BeEmpty("approvals are written through IApprovalLedger only, with their trace: a method of the store that takes approvals is a write door");
    }

    [Fact]
    public async Task The_in_memory_ledger_refuses_a_decision_already_there_before_it_traces_anything()
    {
        var ledger = new InMemoryApprovalLedger(_store, _audit, _killSwitch);
        var approval = new Approval(Guid.NewGuid(), ItemId, "example/app", 1, HeadA, [StepKind.Validate], "alice", Now);
        var trace = new AuditEntry(Now, AuditEvents.ApprovalRecorded, "alice", null, null);
        (await ledger.TryRecordAsync([approval], [trace], TestContext.Current.CancellationToken)).Should().Be(LedgerResult.Recorded);

        var again = new Approval(Guid.NewGuid(), ItemId, "example/app", 1, HeadA, [StepKind.Validate], "bob", Now);
        var result = await ledger.TryRecordAsync([again], [trace], TestContext.Current.CancellationToken);

        result.Should().Be(LedgerResult.AlreadyThere);
        _audit.List().Should().ContainSingle("the second attempt left no trace");
        _store.List().Should().ContainSingle();
    }

    [Fact]
    public async Task The_same_person_refused_for_the_same_reason_is_traced_once_in_a_while_so_that_a_loop_cannot_fill_the_trail()
    {
        var service = await Service();
        var reader = new Approver("reader-1", IsPerson: true, IsOperator: false, StepUpAt: null);

        for (var i = 0; i < 5; i++)
        {
            (await Approve(service, approver: reader)).Refusal.Should().Be(ApprovalRefusal.NotAnOperator);
        }

        _audit.List().Should().ContainSingle("the first refusal is traced, the repeats within ten minutes are not");
    }

    [Fact]
    public async Task Another_reason_another_person_or_a_later_time_is_traced_again()
    {
        var service = await Service();
        var reader = new Approver("reader-1", IsPerson: true, IsOperator: false, StepUpAt: null);
        var other = new Approver("reader-2", IsPerson: true, IsOperator: false, StepUpAt: null);

        await Approve(service, approver: reader);
        await Approve(service, approver: other);
        await Approve(service, steps: [], approver: Operator);
        _time.Advance(TimeSpan.FromMinutes(11));
        await Approve(service, approver: reader);

        _audit.List().Select(entry => entry.Subject).Should().Equal("reader-1", "alice", "reader-2", "reader-1");
    }

    [Fact]
    public async Task A_refusal_that_could_not_be_traced_is_traced_the_next_time_it_comes()
    {
        var sink = Substitute.For<IAuditSink>();
        sink.TryRecordAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>()).Returns(false, true);
        var service = await Service(null, sink);
        var reader = new Approver("reader-1", IsPerson: true, IsOperator: false, StepUpAt: null);

        await Approve(service, approver: reader);
        await Approve(service, approver: reader);
        await Approve(service, approver: reader);

        await sink.Received(2).TryRecordAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_loop_of_refusals_while_the_stop_is_engaged_leaves_one_trace()
    {
        var service = await Service();
        await ChangeStop(_killSwitch, engaged: true);

        for (var i = 0; i < 6; i++)
        {
            (await Approve(service)).Refusal.Should().Be(ApprovalRefusal.KillSwitchEngaged);
        }

        _audit.List().Count(entry => entry.Event == AuditEvents.ApprovalRefused).Should().Be(1);
    }

    [Fact]
    public async Task A_refusal_whose_trace_throws_gives_its_slot_back_and_is_traced_the_next_time()
    {
        var sink = Substitute.For<IAuditSink>();
        sink.TryRecordAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>()).Returns(
            _ => ValueTask.FromException<bool>(new StorageUnavailableException()),
            _ => ValueTask.FromResult(true));
        var service = await Service(null, sink);
        var reader = new Approver("reader-1", IsPerson: true, IsOperator: false, StepUpAt: null);

        var first = async () => await Approve(service, approver: reader);
        await first.Should().ThrowAsync<StorageUnavailableException>();
        (await Approve(service, approver: reader)).Refusal.Should().Be(ApprovalRefusal.NotAnOperator);

        await sink.Received(2).TryRecordAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refusal_is_traced_even_when_the_client_has_gone()
    {
        var sink = Substitute.For<IAuditSink>();
        sink.TryRecordAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>()).Returns(true);
        var service = await Service(null, sink);
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();
        var reader = new Approver("reader-1", IsPerson: true, IsOperator: false, StepUpAt: null);

        var result = await service.ApproveAsync(reader, ItemId, Both, Displayed(), Code, gone.Token);

        result.Refusal.Should().Be(ApprovalRefusal.NotAnOperator);
        await sink.Received(1).TryRecordAsync(Arg.Any<AuditEntry>(), CancellationToken.None);
    }

    [Fact]
    public async Task An_engagement_counts_at_once_and_an_approval_already_inside_the_gate_is_recorded_before_it()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = Substitute.For<IAuditSink>();
        sink.TryRecordRangeAsync(Arg.Any<IReadOnlyList<AuditEntry>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entered.SetResult();
            return new ValueTask<bool>(release.Task);
        });
        sink.TryRecordAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>()).Returns(true);
        using var stop = new InMemoryKillSwitch(sink);
        var ledger = new InMemoryApprovalLedger(_store, sink, stop);
        var approval = new Approval(Guid.NewGuid(), ItemId, "example/app", 1, HeadA, [StepKind.Validate], "alice", Now);
        var write = ledger.TryRecordAsync([approval], [new AuditEntry(Now, AuditEvents.ApprovalRecorded, "alice", null, null)], TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var engage = ChangeStop(stop, engaged: true).AsTask();

        stop.Engaged.Should().BeTrue("an engagement counts before it gets the gate: the next approval is refused");
        engage.IsCompleted.Should().BeFalse("it waits for the write that is already inside, which was decided before it");
        release.SetResult(true);
        (await write).Should().Be(LedgerResult.Recorded);
        (await engage).Should().Be(KillSwitchChange.Applied);
        var next = new Approval(Guid.NewGuid(), ItemId, "example/app", 2, HeadB, [StepKind.Validate], "alice", Now);
        (await ledger.TryRecordAsync([next], [], TestContext.Current.CancellationToken)).Should().Be(LedgerResult.Stopped);
    }

    [Fact]
    public async Task A_release_that_finishes_while_an_engagement_waits_cannot_undo_it()
    {
        var releaseTrace = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = Substitute.For<IAuditSink>();
        sink.TryRecordAsync(Arg.Is<AuditEntry>(entry => entry.Event == AuditEvents.KillSwitchReleased), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            inside.SetResult();
            return new ValueTask<bool>(releaseTrace.Task);
        });
        sink.TryRecordAsync(Arg.Is<AuditEntry>(entry => entry.Event == AuditEvents.KillSwitchEngaged), Arg.Any<CancellationToken>()).Returns(true);
        using var stop = new InMemoryKillSwitch(sink, startEngaged: true);
        var release = ChangeStop(stop, engaged: false).AsTask();
        await inside.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var engage = ChangeStop(stop, engaged: true).AsTask();
        releaseTrace.SetResult(true);
        await release;

        stop.Engaged.Should().BeTrue("the engagement was asked for before the release finished, and is still on its way");
        (await engage).Should().Be(KillSwitchChange.Applied);
        stop.Engaged.Should().BeTrue();
        stop.State().Should().Be(new KillSwitchState(Engaged: true, Saved: false));
    }

    // --- the batch of typed tasks (ADR 0017)

    [Fact]
    public async Task An_approval_makes_one_batch_of_typed_tasks_in_order_each_pull_request_validated_then_merged()
    {
        var result = await Approve(await Service());

        var batch = _queue.Runs(10).Should().ContainSingle().Subject;
        var run = _queue.Find(batch.BatchId)!;
        run.Tasks.Select(task => (task.Spec.Sequence, task.Spec.Kind, task.Spec.PullRequest, task.Spec.HeadSha)).Should().Equal(
            (1, TaskKind.ValidatePullRequest, 1, HeadA),
            (2, TaskKind.MergePullRequest, 1, HeadA),
            (3, TaskKind.ValidatePullRequest, 2, HeadB),
            (4, TaskKind.MergePullRequest, 2, HeadB));
        run.Tasks.Select(task => task.Spec.ApprovalId).Distinct().Should().BeEquivalentTo(result.Approvals.Select(approval => approval.Id));
        result.Approvals.Should().OnlyContain(approval => approval.BatchId == batch.BatchId && approval.State == ApprovalState.Waiting);
        run.Summary.State.Should().Be(BatchState.Waiting);
    }

    [Fact]
    public async Task Only_what_was_ticked_becomes_a_task()
    {
        await Approve(await Service(PullRequest(1, HeadA)), steps: [StepKind.Validate]);

        _queue.Find(_queue.Runs(1)[0].BatchId)!.Tasks.Select(task => task.Spec.Kind).Should().Equal(TaskKind.ValidatePullRequest);
    }

    [Fact]
    public async Task A_batch_lives_for_the_time_it_is_given()
    {
        await Approve(await Service());

        var batch = _queue.Runs(1)[0];

        _queue.Find(batch.BatchId)!.Summary.CreatedAt.Should().Be(Now);
        (await _queue.ClaimAsync("executor-1", TestContext.Current.CancellationToken)).Delivery!.Batch.ExpiresAt.Should().Be(Now + Tasks.BatchLifetime);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    public async Task Without_a_code_shaped_like_one_nothing_is_queued(string? code)
    {
        var service = await Service();

        var result = await service.ApproveAsync(Operator, ItemId, Both, Displayed(), code, TestContext.Current.CancellationToken);

        result.Refusal.Should().Be(ApprovalRefusal.SecondFactorRequired);
        _store.List().Should().BeEmpty();
        _queue.Runs(10).Should().BeEmpty();
    }

    [Fact]
    public async Task Spaces_in_the_code_are_welcome_and_the_code_reaches_the_executor_once()
    {
        var service = await Service();

        var result = await service.ApproveAsync(Operator, ItemId, Both, Displayed(), "123 456", TestContext.Current.CancellationToken);

        result.Recorded.Should().BeTrue();
        (await _queue.ClaimAsync("executor-1", TestContext.Current.CancellationToken)).Delivery!.SecondFactorCode.Should().Be("123 456");
    }

    [Fact]
    public async Task A_sandbox_with_no_executor_may_do_without_the_code()
    {
        var forge = new FakeForge { Items = [PullRequest(1, HeadA)] };
        var service = new ApprovalService(await CatalogOf(forge), _store, _killSwitch, _audit, _time, new InMemoryApprovalLedger(_store, _audit, _killSwitch), _journal, Tasks with { RequireSecondFactor = false });

        var result = await service.ApproveAsync(Operator, ItemId, Both, [new SeenPullRequest(1, HeadA)], null, TestContext.Current.CancellationToken);

        result.Recorded.Should().BeTrue();
    }

    [Fact]
    public async Task An_approval_is_refused_when_the_journal_cannot_be_written_and_nothing_is_kept()
    {
        var journal = Substitute.For<IApprovalJournal>();
        journal.TryAppendAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(false);
        _forge.Items = [PullRequest(1, HeadA)];
        var service = new ApprovalService(await CatalogOf(_forge), _store, _killSwitch, _audit, _time, new InMemoryApprovalLedger(_store, _audit, _killSwitch), journal, Tasks);

        var result = await Approve(service, seen: [new SeenPullRequest(1, HeadA)]);

        result.Refusal.Should().Be(ApprovalRefusal.AuditUnavailable);
        _store.List().Should().BeEmpty();
    }

    [Fact]
    public async Task The_journal_receives_the_digest_of_the_batch_that_was_queued()
    {
        var journal = Substitute.For<IApprovalJournal>();
        journal.TryAppendAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true);
        _forge.Items = [PullRequest(1, HeadA)];
        _queue = new InMemoryBatchQueue(_store, _audit, _killSwitch, _time, Tasks);
        var service = new ApprovalService(await CatalogOf(_forge), _store, _killSwitch, _audit, _time, new InMemoryApprovalLedger(_store, _audit, _killSwitch, _queue), journal, Tasks);

        await Approve(service, seen: [new SeenPullRequest(1, HeadA)]);

        var delivered = (await _queue.ClaimAsync("executor-1", TestContext.Current.CancellationToken)).Delivery!.Batch;
        await journal.Received(1).TryAppendAsync(delivered.Id, delivered.Digest(), "alice", Now, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task While_an_approval_is_open_the_same_decision_is_refused_and_when_it_ended_it_may_be_given_again()
    {
        var service = await Service(PullRequest(1, HeadA));
        var seen = new[] { new SeenPullRequest(1, HeadA) };
        var token = TestContext.Current.CancellationToken;
        await Approve(service, seen: seen);

        (await Approve(service, seen: seen)).Refusal.Should().Be(ApprovalRefusal.AlreadyApproved);

        var delivery = (await _queue.ClaimAsync("executor-1", token)).Delivery!;
        (await Approve(service, seen: seen)).Refusal.Should().Be(ApprovalRefusal.AlreadyApproved, "an approval that was taken is still open");
        await _queue.CompleteAsync(delivery.Batch.Id, "executor-1", new BatchOutcome(BatchState.Done, null, [.. delivery.Batch.Tasks.Select(task => new TaskOutcome(task.Id, TaskResult.DryRun, null))]), token);

        (await Approve(service, seen: seen)).Recorded.Should().BeTrue("a dry run spends the approval: the operator approves again for a real run");
        _store.List().Should().HaveCount(2).And.Contain(approval => approval.State == ApprovalState.DryRunDone);
    }

    [Fact]
    public void Only_the_approval_service_depends_on_the_ledger_so_that_it_is_the_one_entry_of_the_queue()
    {
        var holders = typeof(ApprovalService).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && !typeof(IApprovalLedger).IsAssignableFrom(type))
            .Where(type =>
                type.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Any(ctor => ctor.GetParameters().Any(parameter => parameter.ParameterType == typeof(IApprovalLedger)))
                || type.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Any(field => field.FieldType == typeof(IApprovalLedger)))
            .ToList();

        holders.Should().ContainSingle().Which.Should().Be(typeof(ApprovalService));
    }
}
