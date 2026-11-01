using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.WorkItems;
using static Orchestrarr.Core.Test.FleetBuilders;

namespace Orchestrarr.Core.Test;

public sealed class WorkItemBuilderTests
{
    [Fact]
    public void A_repository_with_nothing_wrong_gives_no_work_item()
    {
        var items = WorkItemBuilder.Build(Snapshot([Repository()]));

        items.Should().BeEmpty();
    }

    [Fact]
    public void An_archived_repository_is_ignored_whatever_its_alerts()
    {
        var items = WorkItemBuilder.Build(Snapshot([Repository(archived: true, dependencyAlerts: new AlertCounts(3, 0, 0, 0))]));

        items.Should().BeEmpty();
    }

    [Fact]
    public void Critical_dependency_alerts_make_a_dangerous_work_item_with_a_readable_summary()
    {
        var items = WorkItemBuilder.Build(Snapshot([Repository(dependencyAlerts: new AlertCounts(1, 2, 1, 0))]));

        var item = items.Should().ContainSingle().Which;
        item.Id.Should().Be("repository:example/app");
        item.Severity.Should().Be(Severity.Danger);
        item.Findings.Should().ContainSingle().Which.Summary.Should().Be("4 dependency alerts (1 critical, 2 high, 1 medium)");
    }

    [Theory]
    [InlineData(0, 0, 1, 0, Severity.Warning)]
    [InlineData(0, 0, 0, 2, Severity.Info)]
    [InlineData(0, 1, 0, 0, Severity.Danger)]
    public void The_severity_of_an_alert_finding_follows_its_worst_alert(int critical, int high, int medium, int low, Severity expected)
    {
        var items = WorkItemBuilder.Build(Snapshot([Repository(codeScanning: new AlertCounts(critical, high, medium, low))]));

        items.Should().ContainSingle().Which.Severity.Should().Be(expected);
    }

    [Fact]
    public void An_exposed_secret_is_dangerous()
    {
        var items = WorkItemBuilder.Build(Snapshot([Repository(secrets: 1)]));

        var item = items.Should().ContainSingle().Which;
        item.Severity.Should().Be(Severity.Danger);
        item.Findings.Should().ContainSingle().Which.Summary.Should().Be("1 exposed secret");
    }

    [Fact]
    public void Waiting_dependency_pull_requests_and_a_failing_ci_are_findings()
    {
        var items = WorkItemBuilder.Build(Snapshot([Repository(pullRequests: 3, ci: CiState.Failing)]));

        var item = items.Should().ContainSingle().Which;
        item.Findings.Select(f => f.Kind).Should().BeEquivalentTo([FindingKind.OpenDependencyPullRequests, FindingKind.FailingCi]);
        item.Severity.Should().Be(Severity.Warning);
    }

    [Fact]
    public void The_plan_validates_merges_and_deploys_to_each_environment_where_the_image_runs_all_ticked()
    {
        var image = Image(repository: "example/app", placements: [Place("b", "Server B"), Place("a", "Server A"), Place("a", "Server A")]);

        var item = WorkItemBuilder.Build(Snapshot([Repository(pullRequests: 1)], [image])).Should().ContainSingle().Which;

        item.Plan.Select(s => (s.Kind, s.Label, s.Ticked, s.Available)).Should().Equal(
            (StepKind.Validate, "Build and test the change", true, true),
            (StepKind.Merge, "Merge the pull requests that pass", true, true),
            (StepKind.Deploy, "Deploy to Server A", true, true),
            (StepKind.Deploy, "Deploy to Server B", true, true));
    }

    [Fact]
    public void Without_a_known_placement_the_deploy_step_is_unavailable_and_not_ticked_and_says_why()
    {
        var item = WorkItemBuilder.Build(Snapshot([Repository(pullRequests: 1)])).Should().ContainSingle().Which;

        var deploy = item.Plan.Should().ContainSingle(s => s.Kind == StepKind.Deploy).Which;
        deploy.Available.Should().BeFalse();
        deploy.Ticked.Should().BeFalse();
        deploy.Reason.Should().Be(WorkItemBuilder.NoPlacementReason);
    }

    [Fact]
    public void An_outdated_image_built_from_a_repository_joins_that_repository_instead_of_making_its_own_work_item()
    {
        var image = Image("ghcr.io/example/app:1.0.0", repository: "example/app", placements: Place("a"));

        var items = WorkItemBuilder.Build(Snapshot([Repository(pullRequests: 1)], [image]));

        var item = items.Should().ContainSingle().Which;
        item.Findings.Select(f => f.Kind).Should().Contain(FindingKind.OutdatedImage);
    }

    [Fact]
    public void An_image_listed_by_the_repository_joins_it_even_when_the_image_does_not_name_it()
    {
        var image = Image("ghcr.io/example/app:1.0.0", placements: Place("a"));

        var items = WorkItemBuilder.Build(Snapshot([Repository(images: ["ghcr.io/example/app:1.0.0"])], [image]));

        items.Should().ContainSingle().Which.Id.Should().Be("repository:example/app");
    }

    [Fact]
    public void A_third_party_image_makes_a_work_item_with_only_deploy_steps()
    {
        var image = Image("grafana/grafana:11.2.0", placements: Place("a", "Cluster"));

        var item = WorkItemBuilder.Build(Snapshot(images: [image])).Should().ContainSingle().Which;

        item.Id.Should().Be("image:grafana/grafana:11.2.0");
        item.RepositoryFullName.Should().BeNull();
        item.Plan.Should().ContainSingle().Which.Label.Should().Be("Deploy to Cluster");
    }

    [Theory]
    [InlineData(ImageStatus.UpToDate)]
    [InlineData(ImageStatus.Floating)]
    [InlineData(ImageStatus.Unknown)]
    [InlineData(ImageStatus.Pending)]
    public void Only_a_behind_or_newer_build_image_is_a_finding(ImageStatus status)
    {
        var items = WorkItemBuilder.Build(Snapshot(images: [Image(status: status, placements: Place("a"))]));

        items.Should().BeEmpty();
    }

    [Fact]
    public void A_major_image_update_is_a_warning_and_a_patch_is_info()
    {
        var major = Image("a:1", change: ImageChange.Major, placements: Place("x"));
        var patch = Image("b:1", change: ImageChange.Patch, placements: Place("x"));

        var items = WorkItemBuilder.Build(Snapshot(images: [major, patch]));

        items.Select(i => (i.Title, i.Severity)).Should().Equal(("a:1", Severity.Warning), ("b:1", Severity.Info));
    }

    [Fact]
    public void Work_items_are_ordered_by_severity_then_title()
    {
        var items = WorkItemBuilder.Build(Snapshot([
            Repository("example/b", pullRequests: 1),
            Repository("example/a", pullRequests: 1),
            Repository("example/z", secrets: 1),
        ]));

        items.Select(i => i.Title).Should().Equal("example/z", "example/a", "example/b");
    }
}
