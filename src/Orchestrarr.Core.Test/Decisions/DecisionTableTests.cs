using Orchestrarr.Core.Applications;
using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Test.Decisions;

/// <summary>The table: one row per problem, and how far each goes is the lowest of what the description allows, what the runners accept and the state of the problem (ADR 0019).</summary>
public sealed class DecisionTableTests
{
    private const string Id = "repository:example/shop";
    private static readonly DateTimeOffset At = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Head = new('a', 40);

    private static PullRequestInfo Pull(int number = 1, UpdateKind kind = UpdateKind.Minor, ChecksState checks = ChecksState.Passing, MergeState merge = MergeState.Clean, bool draft = false, string head = "") =>
        new(number, $"Bump a from 1.0.0 to 2.0.{number}", new Uri($"https://github.com/example/shop/pull/{number}"), "dependabot[bot]", "dependabot/npm", draft, checks, merge, [],
            [new DependencyChange("lodash", "1.0.0", "1.1.0", kind)], At, head.Length == 0 ? Head : head);

    private static PlanStep Step(StepKind kind, bool available = true, bool granted = true) => new(kind, null, null, true, available, granted, null);

    private static WorkItem Item(IReadOnlyList<PullRequestInfo>? pulls = null, IReadOnlyList<Finding>? findings = null, PullRequestsRead? read = null, bool grantMerge = true, bool grantValidate = true, string id = Id, bool grantRepair = true) =>
        new(id, "example/shop", "example/shop", [], Severity.Info, findings ?? [new Finding(FindingKind.OpenDependencyPullRequests, Severity.Info, (pulls ?? [Pull()]).Count)],
            [Step(StepKind.Validate, granted: grantValidate), Step(StepKind.MergePullRequests, granted: grantMerge), Step(StepKind.Repair, granted: grantRepair)], [],
            read ?? new PullRequestsRead(PullRequestsStatus.Read, pulls ?? [Pull()]));

    private static IApplicationCatalog Described(string mode = "smoke", bool rollback = true, bool check = true, string promote = "\"patch\", \"minor\"") =>
        ApplicationCatalog.Parse($$"""
            { "applications": [ { "id": "{{Id}}", "kind": "own", "source": "example/shop",
              "qualify": { "mode": "{{mode}}" },
              "promote": { {{(rollback ? "\"rollback\": \"previous-tag\"" : "\"backup\": \"x\"")}}{{(check ? ", \"check\": \"/health\"" : string.Empty)}} },
              "update": { "promote": [{{promote}}] } } ] }
            """);

    private static IReadOnlyList<ProblemRow> Table(WorkItem item, IApplicationCatalog? applications = null, IReadOnlyList<RunnerAcceptance>? runners = null, IReadOnlyList<TaskResultRow>? results = null) =>
        DecisionTable.Build([item], applications ?? Described(), runners ?? [Everything], results);

    private static readonly RunnerAcceptance Everything = new([TaskKind.ValidatePullRequest, TaskKind.MergePullRequest], ["example/*"]);

    private static readonly RunnerAcceptance Repairing = new([TaskKind.ValidatePullRequest, TaskKind.MergePullRequest, TaskKind.RepairPullRequest], ["example/*"]);

    [Fact]
    public void A_repository_that_is_one_application_of_the_source_takes_its_name_and_its_icon_and_keeps_the_repository_as_the_source()
    {
        var item = Item() with { AppIds = ["shop"] };

        var row = DecisionTable.Build([item], Described(), [Everything], null, [new ApplicationInfo("shop", "Shop", "shop-icon"), new ApplicationInfo("other", "Other", null)]).Single();

        row.Application.Should().Be("Shop");
        row.ApplicationId.Should().Be("shop");
        row.Icon.Should().Be("shop-icon");
        row.Source.Should().Be("example/shop");
    }

    [Fact]
    public void A_repository_of_several_applications_or_of_none_known_keeps_its_own_name_with_no_icon()
    {
        var names = new[] { new ApplicationInfo("a", "A", "a"), new ApplicationInfo("b", "B", "b") };

        var several = DecisionTable.Build([Item() with { AppIds = ["a", "b"] }], Described(), [Everything], null, names).Single();
        var none = DecisionTable.Build([Item() with { AppIds = ["unknown"] }], Described(), [Everything], null, names).Single();
        var unnamed = DecisionTable.Build([Item()], Described(), [Everything]).Single();

        new[] { several, none, unnamed }.Should().OnlyContain(row => row.Application == "example/shop" && row.Icon == null && row.ApplicationId == null && row.Source == "example/shop");
    }

    [Fact]
    public void An_image_used_by_one_application_is_that_application_and_the_findings_of_a_repository_follow_too()
    {
        var finding = new Finding(FindingKind.OutdatedImage, Severity.Info, 1, Image: new ImageUpdate("postgres:15-alpine", "15", "18", Orchestrarr.Core.Fleet.ImageChange.Major));
        var image = new WorkItem("image:postgres:15-alpine", "postgres:15-alpine", null, ["db"], Severity.Info, [finding], [], [], PullRequestsRead.NotNeeded);

        var row = DecisionTable.Build([image], Described(), [Everything], null, [new ApplicationInfo("db", "Database", "postgres")]).Single();

        row.Application.Should().Be("Database");
        row.Source.Should().Be("postgres:15-alpine");
        row.Description.Should().Contain("postgres:15-alpine");
    }

    [Fact]
    public void Checks_that_could_not_run_for_a_bot_stop_at_the_validation_with_their_own_reason_and_are_not_a_repair()
    {
        var row = Only(TableWith(Item([Pull(checks: ChecksState.NotRun)])));

        row.GoesUpTo.Should().Be(Ceiling.Qualification);
        row.Why.Should().Be(CeilingReason.ChecksNotRun);
    }

    [Theory]
    [InlineData("23-alpine", false)]
    [InlineData("25-alpine", false)]
    [InlineData("24-alpine", true)]
    [InlineData("26-alpine", true)]
    public void Node_is_moved_only_to_a_long_term_release_and_the_odd_ones_are_not_proposed(string to, bool proposed)
    {
        var pull = Pull() with { Changes = [new DependencyChange("node", "22-alpine", to, UpdateKind.Major)] };

        var row = Only(Table(Item([pull])));

        (row.GoesUpTo != Ceiling.NoAction).Should().Be(proposed);
        if (!proposed)
        {
            row.Why.Should().Be(CeilingReason.NotLongTermSupport);
        }
    }

    private static WorkItem Dormant(DateTimeOffset lastPush, bool failingCi = true, int pulls = 2) =>
        Item([.. Enumerable.Range(1, pulls).Select(n => Pull(n))], [new Finding(FindingKind.OpenDependencyPullRequests, Severity.Info, pulls), .. failingCi ? new[] { new Finding(FindingKind.FailingCi, Severity.Warning, 1) } : []]) with { LastPushAt = lastPush };

    [Fact]
    public void A_repository_nobody_has_pushed_to_for_months_with_a_red_build_is_one_row_instead_of_its_pull_requests_and_its_build()
    {
        var now = At.AddDays(200);

        var rows = DecisionTable.Build([Dormant(At)], Described(), [Everything], null, null, now);

        var row = rows.Should().ContainSingle().Which;
        row.Kind.Should().Be(ProblemKind.DormantRepository);
        row.Count.Should().Be(200);
    }

    [Fact]
    public void A_dormant_repository_keeps_its_exposed_secrets_and_alerts_as_rows_of_their_own()
    {
        var item = Dormant(At) with { Findings = [.. Dormant(At).Findings, new Finding(FindingKind.SecretScanningAlerts, Severity.Danger, 1), new Finding(FindingKind.DependencyAlerts, Severity.Warning, 3)] };

        var rows = DecisionTable.Build([item], Described(), [Everything], null, null, At.AddDays(200));

        rows.Select(row => row.Kind).Should().BeEquivalentTo([ProblemKind.DormantRepository, ProblemKind.ExposedSecret, ProblemKind.DependencyAlert]);
    }

    [Fact]
    public void The_text_of_a_dormant_row_does_not_change_from_one_day_to_the_next_so_that_a_skip_holds()
    {
        var first = DecisionTable.Build([Dormant(At)], Described(), [Everything], null, null, At.AddDays(200)).Single();
        var later = DecisionTable.Build([Dormant(At)], Described(), [Everything], null, null, At.AddDays(230)).Single();

        later.Description.Should().Be(first.Description);
        later.Count.Should().Be(230, "the number of days is the count, not part of the text a skip is bound to");
    }

    [Fact]
    public void A_policy_with_a_longer_delay_keeps_the_repository_out_of_the_dormant_row()
    {
        var policy = RelevancePolicy.Default with { DormantAfterDays = 365 };

        DecisionTable.Build([Dormant(At)], Described(), [Everything], null, null, At.AddDays(200), policy).Should().HaveCountGreaterThan(1);
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(200, false)]
    public void A_dormant_repository_needs_a_red_build_and_enough_months_and_a_known_time(int days, bool failingCi)
    {
        var now = At.AddDays(days);

        DecisionTable.Build([Dormant(At, failingCi)], Described(), [Everything], null, null, now).Should().HaveCountGreaterThan(1, "it is not dormant, so its rows stay");
        DecisionTable.Build([Dormant(At, failingCi: true)], Described(), [Everything], null, null, null).Should().HaveCountGreaterThan(1, "without a clock nothing is called dormant");
    }

    private static WorkItem ImageItem(string reference, Orchestrarr.Core.Fleet.ImageChange change, params string?[] namespaces) =>
        new($"image:{reference}", reference, null, [], Severity.Info, [new Finding(FindingKind.OutdatedImage, Severity.Info, 1, Image: new ImageUpdate(reference, "1", "2", change))], [],
            [.. namespaces.Select(space => new Placement("k3s", "K3s", space, "pod"))], PullRequestsRead.NotNeeded);

    [Fact]
    public void An_image_of_the_platform_is_updated_with_it_and_a_major_image_is_a_change_to_plan_and_neither_is_a_tag_to_bump()
    {
        var platform = DecisionTable.Build([ImageItem("rancher/coredns:1", Orchestrarr.Core.Fleet.ImageChange.Minor, "kube-system")], Described(), [Everything]).Single();
        var major = DecisionTable.Build([ImageItem("postgres:15", Orchestrarr.Core.Fleet.ImageChange.Major, "apps")], Described(), [Everything]).Single();
        var minor = DecisionTable.Build([ImageItem("influxdb:2.7", Orchestrarr.Core.Fleet.ImageChange.Minor, "apps")], Described(), [Everything]).Single();
        var mixed = DecisionTable.Build([ImageItem("shared:1", Orchestrarr.Core.Fleet.ImageChange.Minor, "kube-system", "apps")], Described(), [Everything]).Single();

        platform.Why.Should().Be(CeilingReason.ManagedByPlatform);
        major.Why.Should().Be(CeilingReason.MajorNeedsPlan);
        minor.Why.Should().Be(CeilingReason.NoActionDefined);
        mixed.Why.Should().Be(CeilingReason.NoActionDefined, "an image that also runs outside the platform is not only the platform's");
    }

    [Fact]
    public void An_image_with_a_moving_tag_is_a_row_named_by_the_image_alone()
    {
        var finding = new Finding(FindingKind.FloatingImage, Severity.Info, 1, Image: new ImageUpdate("adminer:latest", null, null, Orchestrarr.Core.Fleet.ImageChange.None));
        var item = new WorkItem("image:adminer:latest", "adminer:latest", null, [], Severity.Info, [finding], [], [], PullRequestsRead.NotNeeded);

        var row = DecisionTable.Build([item], Described(), [Everything]).Single();

        row.Kind.Should().Be(ProblemKind.FloatingImage);
        row.Description.Should().Be("adminer:latest");
    }

    private static ProblemRow Only(IReadOnlyList<ProblemRow> rows) => rows.Single(row => row.Kind == ProblemKind.DependencyUpdate);

    [Fact]
    public void A_passing_update_of_a_class_that_may_go_by_itself_goes_up_to_production_and_can_be_stopped()
    {
        var row = Only(Table(Item()));

        row.GoesUpTo.Should().Be(Ceiling.Production);
        row.Why.Should().Be(CeilingReason.None);
        row.CanStopBeforeProduction.Should().BeTrue();
        row.Id.Should().Be("pr:example/shop#1");
        row.Description.Should().Be("lodash 1.0.0 → 1.1.0");
        row.PullRequest.Should().Be(1);
        row.HeadSha.Should().Be(Head);
        row.UpdateClass.Should().Be(UpdateKind.Minor);
    }

    [Fact]
    public void A_class_of_update_that_is_not_promoted_by_itself_stops_at_the_merge()
    {
        var row = Only(Table(Item([Pull(kind: UpdateKind.Major)])));

        row.GoesUpTo.Should().Be(Ceiling.MergeIntoDevelop);
        row.Why.Should().Be(CeilingReason.UpdateClassStopsBeforeProduction);
        row.CanStopBeforeProduction.Should().BeFalse("it already stops before production");
    }

    [Fact]
    public void Production_needs_a_rollback_and_a_check_after_the_deployment_to_be_described()
    {
        var noRollback = Only(Table(Item(), Described(rollback: false)));
        noRollback.GoesUpTo.Should().Be(Ceiling.MergeIntoDevelop);
        noRollback.Why.Should().Be(CeilingReason.NoRollbackDescribed);

        var noCheck = Only(Table(Item(), Described(check: false)));
        noCheck.GoesUpTo.Should().Be(Ceiling.MergeIntoDevelop);
        noCheck.Why.Should().Be(CeilingReason.NoCheckAfterDeployDescribed);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("unknown")]
    public void An_application_that_cannot_say_how_to_know_it_works_is_qualified_but_never_merged(string mode)
    {
        var row = Only(Table(Item(), Described(mode)));

        row.GoesUpTo.Should().Be(Ceiling.Qualification);
        row.Why.Should().Be(CeilingReason.QualificationNotDescribed);
    }

    private static TaskResultRow Said(TaskKind kind, TaskResult result, string? reason = null, int minutes = 0, string? head = null) =>
        new("example/shop", 1, head ?? Head, kind, result, reason, At.AddMinutes(minutes));

    private static IReadOnlyList<ProblemRow> TableWith(WorkItem item, params TaskResultRow[] results) =>
        DecisionTable.Build([item], Described(), [Repairing], results);

    [Fact]
    public void A_pull_request_whose_checks_fail_on_the_forge_goes_up_to_the_repair_where_one_can_be_tried()
    {
        var row = Only(TableWith(Item([Pull(checks: ChecksState.Failing)])));

        row.GoesUpTo.Should().Be(Ceiling.Repair);
        row.Why.Should().Be(CeilingReason.ChecksFailing);
        DecisionTable.StepsOf(row.GoesUpTo).Should().Equal(StepKind.Repair);
    }

    [Fact]
    public void Where_no_repair_can_be_tried_a_failing_pull_request_stays_at_the_qualification()
    {
        Only(Table(Item([Pull(checks: ChecksState.Failing)]), runners: [Everything])).GoesUpTo.Should().Be(Ceiling.Qualification, "no runner accepts the repair");
        Only(TableWith(Item([Pull(checks: ChecksState.Failing)], grantRepair: false))).GoesUpTo.Should().Be(Ceiling.Qualification, "the repair is not granted");
    }

    [Fact]
    public void A_pull_request_that_passes_on_the_forge_but_failed_in_the_workshop_becomes_a_repair()
    {
        var row = Only(TableWith(Item(), Said(TaskKind.ValidatePullRequest, TaskResult.Failed, "npm test failed (exit 1)")));

        row.GoesUpTo.Should().Be(Ceiling.Repair);
        row.Why.Should().Be(CeilingReason.QualificationFailed);
        row.Workshop.Should().Be(WorkshopVerdict.Fails);
    }

    [Fact]
    public void The_latest_word_of_the_workshop_on_the_commit_counts_and_a_new_commit_starts_again()
    {
        var failedThenPassed = TableWith(Item(), Said(TaskKind.ValidatePullRequest, TaskResult.Failed, minutes: 0), Said(TaskKind.ValidatePullRequest, TaskResult.Passed, minutes: 5));
        var other = TableWith(Item(), Said(TaskKind.ValidatePullRequest, TaskResult.Failed, head: new string('f', 40)));

        Only(failedThenPassed).GoesUpTo.Should().Be(Ceiling.Production, "it passed since");
        Only(other).GoesUpTo.Should().Be(Ceiling.Production, "the failure was on another commit");
    }

    [Fact]
    public void A_repair_that_did_not_work_can_be_tried_again()
    {
        var row = Only(TableWith(Item([Pull(checks: ChecksState.Failing)]), Said(TaskKind.RepairPullRequest, TaskResult.Failed, "the agent made no change")));

        row.GoesUpTo.Should().Be(Ceiling.Repair);
        row.Why.Should().Be(CeilingReason.RepairFailed);
    }

    [Fact]
    public void A_repair_that_worked_leaves_a_patch_and_nothing_to_select_because_applying_it_is_not_built()
    {
        var row = Only(TableWith(Item([Pull(checks: ChecksState.Failing)]), Said(TaskKind.RepairPullRequest, TaskResult.Repaired, "repaired: 1 file(s) changed")));

        row.GoesUpTo.Should().Be(Ceiling.NoAction);
        row.Why.Should().Be(CeilingReason.PatchReady);
        row.Workshop.Should().Be(WorkshopVerdict.Repaired);
    }

    [Fact]
    public void A_task_that_was_refused_or_only_dry_run_after_a_failure_says_nothing_about_the_code_so_the_failure_still_counts()
    {
        var row = Only(TableWith(Item(), Said(TaskKind.ValidatePullRequest, TaskResult.Failed, minutes: 0), Said(TaskKind.ValidatePullRequest, TaskResult.Refused, minutes: 5), Said(TaskKind.MergePullRequest, TaskResult.DryRun, minutes: 6)));

        row.Workshop.Should().Be(WorkshopVerdict.Fails);
        row.GoesUpTo.Should().Be(Ceiling.Repair);
    }

    [Fact]
    public void When_the_workshop_failed_and_no_repair_can_be_tried_the_pull_request_stays_at_the_qualification()
    {
        var row = Only(Table(Item(grantRepair: false), runners: [Repairing], results: [Said(TaskKind.ValidatePullRequest, TaskResult.Failed)]));

        row.GoesUpTo.Should().Be(Ceiling.Qualification);
        row.Why.Should().Be(CeilingReason.QualificationFailed);
    }

    [Fact]
    public void A_newer_validation_that_passes_after_a_repair_is_the_latest_word()
    {
        var row = Only(TableWith(Item(), Said(TaskKind.RepairPullRequest, TaskResult.Repaired, "repaired: 1 file(s) changed", minutes: 0), Said(TaskKind.ValidatePullRequest, TaskResult.Passed, minutes: 5)));

        row.Workshop.Should().Be(WorkshopVerdict.Passes);
        row.Why.Should().NotBe(CeilingReason.PatchReady);
    }

    [Fact]
    public void A_pull_request_that_passes_in_the_workshop_while_the_forge_fails_points_at_the_pipeline_not_at_a_repair()
    {
        var row = Only(TableWith(Item([Pull(checks: ChecksState.Failing)]), Said(TaskKind.RepairPullRequest, TaskResult.Passed, "nothing to repair: the validation passes")));

        row.GoesUpTo.Should().Be(Ceiling.Qualification);
        row.Why.Should().Be(CeilingReason.PassesHereChecksFailing);
        row.Workshop.Should().Be(WorkshopVerdict.Passes);
    }

    [Fact]
    public void A_commit_the_workshop_never_ran_has_no_verdict()
    {
        Only(TableWith(Item())).Workshop.Should().Be(WorkshopVerdict.Unknown);
    }

    [Theory]
    [InlineData(ChecksState.Failing, CeilingReason.ChecksFailing)]
    [InlineData(ChecksState.Pending, CeilingReason.ChecksNotPassing)]
    [InlineData(ChecksState.Unknown, CeilingReason.ChecksNotPassing)]
    public void Checks_that_do_not_pass_keep_the_update_at_the_qualification(ChecksState checks, CeilingReason why)
    {
        var row = Only(Table(Item([Pull(checks: checks)])));

        row.GoesUpTo.Should().Be(Ceiling.Qualification);
        row.Why.Should().Be(why);
    }

    [Theory]
    [InlineData(MergeState.Conflicting)]
    [InlineData(MergeState.Behind)]
    [InlineData(MergeState.Blocked)]
    [InlineData(MergeState.Unstable)]
    public void A_pull_request_the_forge_will_not_merge_as_it_stands_stays_at_the_qualification(MergeState merge)
    {
        var row = Only(Table(Item([Pull(merge: merge)])));

        row.GoesUpTo.Should().Be(Ceiling.Qualification);
        row.Why.Should().Be(CeilingReason.CannotBeMergedNow);
    }

    [Fact]
    public void An_application_with_no_description_has_no_action_and_the_row_says_why()
    {
        var row = Only(Table(Item(), new EmptyApplicationCatalog()));

        row.GoesUpTo.Should().Be(Ceiling.NoAction);
        row.Why.Should().Be(CeilingReason.NoDescription);
    }

    [Fact]
    public void The_powers_granted_to_orchestrarr_bound_the_row()
    {
        var noValidate = Only(Table(Item(grantValidate: false)));
        noValidate.GoesUpTo.Should().Be(Ceiling.NoAction);
        noValidate.Why.Should().Be(CeilingReason.NotGranted);

        var noMerge = Only(Table(Item(grantMerge: false)));
        noMerge.GoesUpTo.Should().Be(Ceiling.Qualification);
        noMerge.Why.Should().Be(CeilingReason.NotGranted);
    }

    [Fact]
    public void The_runners_that_are_connected_bound_the_row_to_what_they_accept()
    {
        var all = new RunnerAcceptance([TaskKind.ValidatePullRequest, TaskKind.MergePullRequest], ["example/*"]);
        var validateOnly = new RunnerAcceptance([TaskKind.ValidatePullRequest], ["example/shop"]);
        var elsewhere = new RunnerAcceptance([TaskKind.ValidatePullRequest, TaskKind.MergePullRequest], ["other/*"]);

        Only(Table(Item(), runners: [all])).GoesUpTo.Should().Be(Ceiling.Production);
        var nobody = Only(Table(Item(), runners: []));
        nobody.GoesUpTo.Should().Be(Ceiling.NoAction, "with no runner connected nothing can be run, and the row says so");
        nobody.Why.Should().Be(CeilingReason.NoRunner);
        var capped = Only(Table(Item(), runners: [validateOnly]));
        capped.GoesUpTo.Should().Be(Ceiling.Qualification);
        capped.Why.Should().Be(CeilingReason.NoRunner);
        var none = Only(Table(Item(), runners: [elsewhere]));
        none.GoesUpTo.Should().Be(Ceiling.NoAction);
        none.Why.Should().Be(CeilingReason.NoRunner);
        Only(Table(Item(), runners: [validateOnly, all])).GoesUpTo.Should().Be(Ceiling.Production, "it is enough that one runner accepts");
    }

    [Fact]
    public void A_pull_request_list_that_is_stale_or_without_a_commit_cannot_be_acted_on()
    {
        var stale = Table(Item(read: new PullRequestsRead(PullRequestsStatus.Failed, [Pull()], Stale: true)));
        Only(stale).GoesUpTo.Should().Be(Ceiling.NoAction);
        Only(stale).Why.Should().Be(CeilingReason.PullRequestsNotCurrent);

        Only(Table(Item([Pull(head: "x")]))).GoesUpTo.Should().Be(Ceiling.Production, "a commit is whatever the forge reads");
        var noCommit = Pull() with { HeadSha = string.Empty };
        Only(Table(Item([noCommit]))).GoesUpTo.Should().Be(Ceiling.NoAction);
    }

    [Fact]
    public void A_draft_is_not_a_problem_to_decide()
    {
        Table(Item([Pull(1), Pull(2, draft: true)])).Where(row => row.Kind == ProblemKind.DependencyUpdate).Should().ContainSingle().Which.PullRequest.Should().Be(1);
    }

    [Fact]
    public void A_pull_request_that_changes_several_packages_is_worded_by_its_title()
    {
        var several = Pull() with { Changes = [new DependencyChange("a", null, null, UpdateKind.Minor), new DependencyChange("b", null, null, UpdateKind.Minor)] };

        Only(Table(Item([several]))).Description.Should().Be(several.Title);
        Only(Table(Item([several]))).UpdateClass.Should().Be(UpdateKind.Unknown);
    }

    [Fact]
    public void The_other_findings_are_rows_with_no_action_defined_yet()
    {
        var findings = new[]
        {
            new Finding(FindingKind.DependencyAlerts, Severity.Danger, 2, new AlertCounts(1, 1, 0, 0)),
            new Finding(FindingKind.CodeScanningAlerts, Severity.Warning, 1),
            new Finding(FindingKind.SecretScanningAlerts, Severity.Danger, 1),
            new Finding(FindingKind.FailingCi, Severity.Warning, 1),
            new Finding(FindingKind.OutdatedImage, Severity.Info, 1, Image: new ImageUpdate("ghcr.io/example/app:1", "1", "2", ImageChange.Minor)),
            new Finding(FindingKind.DependencyScanUnavailable, Severity.Info, 1),
        };

        var rows = Table(Item(pulls: [], findings: findings, read: PullRequestsRead.NotNeeded));

        rows.Select(row => row.Kind).Should().Equal(ProblemKind.DependencyAlert, ProblemKind.CodeScanningAlert, ProblemKind.ExposedSecret, ProblemKind.FailingCi, ProblemKind.ImageUpdate, ProblemKind.DependencyScanUnavailable);
        rows.Should().OnlyContain(row => row.GoesUpTo == Ceiling.NoAction && row.Why == CeilingReason.NoActionDefined && row.PullRequest == null && !row.CanStopBeforeProduction);
        rows.Single(row => row.Kind == ProblemKind.DependencyAlert).Should().Match<ProblemRow>(row => row.Count == 2 && row.Severity == Severity.Danger);
        rows.Single(row => row.Kind == ProblemKind.ImageUpdate).Description.Should().Be("ghcr.io/example/app:1 → 2");
        rows.Select(row => row.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Pull_requests_that_wait_but_were_not_read_are_a_row_that_says_so_and_a_read_that_worked_adds_none()
    {
        var waiting = Table(Item(pulls: [], read: PullRequestsRead.Pending, findings: [new Finding(FindingKind.OpenDependencyPullRequests, Severity.Info, 3)]));
        waiting.Should().ContainSingle().Which.Should().Match<ProblemRow>(row => row.Kind == ProblemKind.PullRequestsNotRead && row.Count == 3 && row.GoesUpTo == Ceiling.NoAction);

        Table(Item()).Should().OnlyContain(row => row.Kind == ProblemKind.DependencyUpdate);
    }

    [Theory]
    [InlineData(Ceiling.NoAction, new StepKind[0])]
    [InlineData(Ceiling.Qualification, new[] { StepKind.Validate })]
    [InlineData(Ceiling.MergeIntoDevelop, new[] { StepKind.Validate, StepKind.MergePullRequests })]
    [InlineData(Ceiling.Production, new[] { StepKind.Validate, StepKind.MergePullRequests })]
    public void The_steps_a_ceiling_stands_for(Ceiling ceiling, StepKind[] steps)
    {
        DecisionTable.StepsOf(ceiling).Should().Equal(steps);
    }
}
