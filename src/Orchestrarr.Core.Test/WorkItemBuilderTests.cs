using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.WorkItems;
using static Orchestrarr.Core.Test.FleetBuilders;

namespace Orchestrarr.Core.Test;

public sealed class WorkItemBuilderTests
{
    [Fact]
    public void A_repository_with_nothing_wrong_gives_no_work_item()
    {
        Build(Snapshot([Repository()])).Should().BeEmpty();
    }

    [Fact]
    public void An_archived_repository_and_a_fork_are_ignored_whatever_their_alerts()
    {
        var alerts = new AlertCounts(3, 0, 0, 0);

        Build(Snapshot([Repository("example/old", archived: true, dependencyAlerts: alerts), Repository("example/fork", fork: true, dependencyAlerts: alerts)]))
            .Should().BeEmpty();
    }

    [Fact]
    public void Critical_dependency_alerts_make_a_dangerous_work_item_that_carries_the_numbers()
    {
        var item = Build(Snapshot([Repository(dependencyAlerts: new AlertCounts(1, 2, 1, 0))])).Should().ContainSingle().Which;

        item.Id.Should().Be("repository:example/app");
        item.Severity.Should().Be(Severity.Danger);
        var finding = item.Findings.Should().ContainSingle().Which;
        finding.Kind.Should().Be(FindingKind.DependencyAlerts);
        finding.Count.Should().Be(4);
        finding.Alerts.Should().Be(new AlertCounts(1, 2, 1, 0));
    }

    [Theory]
    [InlineData(0, 0, 1, 0, Severity.Warning)]
    [InlineData(0, 0, 0, 2, Severity.Info)]
    [InlineData(0, 1, 0, 0, Severity.Danger)]
    public void The_severity_of_an_alert_finding_follows_its_worst_alert(int critical, int high, int medium, int low, Severity expected)
    {
        var item = Build(Snapshot([Repository(codeScanning: new AlertCounts(critical, high, medium, low))])).Should().ContainSingle().Which;

        item.Findings.Should().ContainSingle().Which.Kind.Should().Be(FindingKind.CodeScanningAlerts);
        item.Severity.Should().Be(expected);
    }

    [Fact]
    public void An_exposed_secret_is_dangerous_and_has_no_plan_because_only_a_person_can_revoke_it()
    {
        var item = Build(Snapshot([Repository(secrets: 2)])).Should().ContainSingle().Which;

        item.Severity.Should().Be(Severity.Danger);
        item.Findings.Should().ContainSingle().Which.Should().Be(new Finding(FindingKind.SecretScanningAlerts, Severity.Danger, 2));
        item.Plan.Should().BeEmpty();
    }

    [Theory]
    [InlineData(AlertScanState.Off)]
    [InlineData(AlertScanState.Unknown)]
    public void A_dependency_scan_that_is_off_or_unreadable_is_not_a_clean_repository(AlertScanState state)
    {
        var item = Build(Snapshot([Repository(dependencyScan: state)])).Should().ContainSingle().Which;

        item.Findings.Should().ContainSingle().Which.Kind.Should().Be(FindingKind.DependencyScanUnavailable);
        item.Severity.Should().Be(Severity.Info);
        item.Plan.Should().BeEmpty();
    }

    [Fact]
    public void Alerts_of_a_scan_that_is_off_are_not_counted()
    {
        var item = Build(Snapshot([Repository(dependencyScan: AlertScanState.Off, dependencyAlerts: new AlertCounts(5, 0, 0, 0))])).Should().ContainSingle().Which;

        item.Findings.Select(f => f.Kind).Should().Equal(FindingKind.DependencyScanUnavailable);
    }

    [Fact]
    public void Waiting_dependency_pull_requests_and_a_failing_ci_are_findings()
    {
        var item = Build(Snapshot([Repository(pullRequests: 3, ci: CiState.Failing)])).Should().ContainSingle().Which;

        item.Findings.Select(f => (f.Kind, f.Count)).Should().BeEquivalentTo([(FindingKind.OpenDependencyPullRequests, 3), (FindingKind.FailingCi, 1)]);
        item.Severity.Should().Be(Severity.Warning);
    }

    [Fact]
    public void The_plan_validates_merges_and_deploys_to_each_environment_where_the_image_runs_all_ticked()
    {
        var image = Image("ghcr.io/example/app:1.0.0", ImageStatus.Behind, ImageChange.Minor, "example/app", Place("b", "Server B"), Place("a", "Server A"), Place("a", "Server A"));

        var item = Build(Snapshot([Repository(pullRequests: 1)], [image])).Should().ContainSingle().Which;

        item.Plan.Select(s => (s.Kind, s.EnvironmentName, s.Ticked, s.Available)).Should().Equal(
            (StepKind.Validate, null, true, true),
            (StepKind.MergePullRequests, null, true, true),
            (StepKind.Deploy, "Server A", true, true),
            (StepKind.Deploy, "Server B", true, true));
    }

    [Fact]
    public void Without_a_known_placement_the_deploy_step_is_unavailable_and_not_ticked_and_says_why()
    {
        var item = Build(Snapshot([Repository(pullRequests: 1)])).Should().ContainSingle().Which;

        var deploy = item.Plan.Should().ContainSingle(s => s.Kind == StepKind.Deploy).Which;
        deploy.Available.Should().BeFalse();
        deploy.Ticked.Should().BeFalse();
        deploy.Reason.Should().Be(StepReason.NoPlacement);
    }

    [Fact]
    public void With_no_pull_request_to_merge_the_merge_step_is_the_merge_of_a_fix()
    {
        var image = Image(repository: "example/app", placements: Place("a"));

        var item = Build(Snapshot([Repository()], [image])).Should().ContainSingle().Which;

        item.Plan.Select(s => s.Kind).Should().Equal(StepKind.Validate, StepKind.MergeFix, StepKind.Deploy);
    }

    [Fact]
    public void No_step_is_granted_while_no_power_is_granted()
    {
        var item = Build(Snapshot([Repository(pullRequests: 1)], [Image(repository: "example/app", placements: Place("a"))])).Should().ContainSingle().Which;

        item.Plan.Should().OnlyContain(s => !s.Granted);
    }

    [Fact]
    public void A_step_is_granted_when_the_policy_grants_it_for_that_repository_and_environment()
    {
        var policy = Substitute.For<IPowerPolicy>();
        policy.IsGranted(StepKind.Validate, "example/app", null).Returns(true);
        policy.IsGranted(StepKind.Deploy, "example/app", "a").Returns(true);
        var image = Image("ghcr.io/example/app:1.0.0", ImageStatus.Behind, ImageChange.Minor, "example/app", Place("a"), Place("b"));

        var item = WorkItemBuilder.Build(Snapshot([Repository(pullRequests: 1)], [image]), policy).Should().ContainSingle().Which;

        item.Plan.Select(s => (s.Kind, s.EnvironmentId, s.Granted)).Should().Equal(
            (StepKind.Validate, null, true),
            (StepKind.MergePullRequests, null, false),
            (StepKind.Deploy, "a", true),
            (StepKind.Deploy, "b", false));
    }

    [Fact]
    public void An_outdated_image_built_from_a_repository_joins_that_repository_instead_of_making_its_own_work_item()
    {
        var image = Image("ghcr.io/example/app:1.0.0", repository: "example/app", placements: Place("a"));

        var item = Build(Snapshot([Repository(pullRequests: 1)], [image])).Should().ContainSingle().Which;

        item.Findings.Select(f => f.Kind).Should().Contain(FindingKind.OutdatedImage);
    }

    [Fact]
    public void An_image_known_by_both_the_repository_and_the_image_is_counted_once()
    {
        var image = Image("ghcr.io/example/app:1.0.0", repository: "example/app", placements: Place("a"));

        var item = Build(Snapshot([Repository(images: ["ghcr.io/example/app:1.0.0"])], [image])).Should().ContainSingle().Which;

        item.Findings.Should().ContainSingle(f => f.Kind == FindingKind.OutdatedImage);
    }

    [Fact]
    public void An_image_listed_by_the_repository_joins_it_even_when_the_image_does_not_name_it()
    {
        var image = Image("ghcr.io/example/app:1.0.0", placements: Place("a"));

        Build(Snapshot([Repository(images: ["ghcr.io/example/app:1.0.0"])], [image])).Should().ContainSingle().Which.Id.Should().Be("repository:example/app");
    }

    [Fact]
    public void The_image_of_an_ignored_repository_becomes_a_third_party_work_item_with_only_deploy_steps()
    {
        var image = Image("ghcr.io/example/old:1.0.0", repository: "example/old", placements: Place("a", "Cluster"));

        var item = Build(Snapshot([Repository("example/old", archived: true)], [image])).Should().ContainSingle().Which;

        item.Id.Should().Be("image:ghcr.io/example/old:1.0.0");
        item.Plan.Should().ContainSingle().Which.Kind.Should().Be(StepKind.Deploy);
    }

    [Fact]
    public void A_third_party_image_makes_a_work_item_with_only_deploy_steps()
    {
        var image = Image("grafana/grafana:11.2.0", placements: Place("a", "Cluster"));

        var item = Build(Snapshot(images: [image])).Should().ContainSingle().Which;

        item.Id.Should().Be("image:grafana/grafana:11.2.0");
        item.RepositoryFullName.Should().BeNull();
        item.Plan.Should().ContainSingle().Which.Should().Match<PlanStep>(s => s.Kind == StepKind.Deploy && s.EnvironmentName == "Cluster");
    }

    [Fact]
    public void An_outdated_image_finding_carries_the_update()
    {
        var image = Image("grafana/grafana:11.2.0", placements: Place("a"));

        var finding = Build(Snapshot(images: [image])).Should().ContainSingle().Which.Findings.Should().ContainSingle().Which;

        finding.Kind.Should().Be(FindingKind.OutdatedImage);
        finding.Image.Should().Be(new ImageUpdate("grafana/grafana:11.2.0", "1.0.0", "1.1.0", ImageChange.Minor));
    }

    [Fact]
    public void A_newer_build_of_the_same_version_is_a_finding_without_versions_to_show()
    {
        var image = new ImageState("app:1", null, null, ImageStatus.NewerBuild, ImageChange.None, null, [Place("a")]);

        var finding = Build(Snapshot(images: [image])).Should().ContainSingle().Which.Findings.Should().ContainSingle().Which;

        finding.Image.Should().Be(new ImageUpdate("app:1", null, null, ImageChange.None));
    }

    [Theory]
    [InlineData(ImageStatus.UpToDate)]
    [InlineData(ImageStatus.Floating)]
    [InlineData(ImageStatus.Unknown)]
    [InlineData(ImageStatus.Pending)]
    public void Only_a_behind_or_newer_build_image_is_a_finding(ImageStatus status)
    {
        Build(Snapshot(images: [Image(status: status, placements: Place("a"))])).Should().BeEmpty();
    }

    [Fact]
    public void A_major_image_update_is_a_warning_and_a_patch_is_info()
    {
        var major = Image("a:1", change: ImageChange.Major, placements: Place("x"));
        var patch = Image("b:1", change: ImageChange.Patch, placements: Place("x"));

        Build(Snapshot(images: [major, patch])).Select(i => (i.Title, i.Severity)).Should().Equal(("a:1", Severity.Warning), ("b:1", Severity.Info));
    }

    [Fact]
    public void Work_items_are_ordered_by_severity_then_title()
    {
        var items = Build(Snapshot([
            Repository("example/b", pullRequests: 1),
            Repository("example/a", pullRequests: 1),
            Repository("example/z", secrets: 1),
        ]));

        items.Select(i => i.Title).Should().Equal("example/z", "example/a", "example/b");
    }
}
