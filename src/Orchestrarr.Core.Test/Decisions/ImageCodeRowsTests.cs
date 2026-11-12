using Orchestrarr.Core.Applications;
using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.Infrastructure;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Test.Decisions;

/// <summary>A row about an image says where its version is written in the code of the scope, and what that changes (ADR 0028).</summary>
public sealed class ImageCodeRowsTests
{
    private static InfrastructureIndex Code(string manifest, string path = "k8s/n8n.yaml") =>
        InfrastructureIndex.Build(new InfrastructureSnapshot(DateTimeOffset.UnixEpoch, [new InfrastructureFile("example/infra", path, "kubernetes", manifest)], []));

    private static WorkItem ImageItem(Finding finding, string? space = null) =>
        new("image:example", "n8n", null, [], Severity.Info, [finding], [], space is null ? [] : [new Placement("k3s", "K3s", space, "pod")], PullRequestsRead.NotNeeded);

    private static Finding Behind(string reference = "n8nio/n8n:1.108.2", string current = "1.108.2", string latest = "1.121.0", ImageChange change = ImageChange.Minor) =>
        new(FindingKind.OutdatedImage, Severity.Info, 1, Image: new ImageUpdate(reference, current, latest, change));

    private static ProblemRow Row(WorkItem item, InfrastructureIndex? index) =>
        DecisionTable.Build([item], new EmptyApplicationCatalog(), [], infrastructure: index).Single();

    [Fact]
    public void An_outdated_image_found_in_the_code_shows_where_and_the_version_the_change_would_write_and_stays_information()
    {
        var row = Row(ImageItem(Behind()), Code("        image: n8nio/n8n:1.108.2\n"));

        row.Why.Should().Be(CeilingReason.CodeChangeShown);
        row.ProposedTag.Should().Be("1.121.0");
        row.Locations.Should().ContainSingle().Which.Should().Match<CodeLocation>(location => location.Repository == "example/infra" && location.Path == "k8s/n8n.yaml" && location.Line == 1);
        row.Tab.Should().Be(ProblemTab.Know, "nothing can be approved: no write power exists");
    }

    [Fact]
    public void An_image_written_in_no_repository_of_the_scope_asks_for_the_description_to_be_completed()
    {
        var row = Row(ImageItem(Behind()), Code("image: other:1\n"));

        row.Why.Should().Be(CeilingReason.NotInScope);
        row.Locations.Should().BeEmpty();
        row.Tab.Should().Be(ProblemTab.Configure);
    }

    [Fact]
    public void Without_a_scope_nothing_is_said_about_the_code_and_the_row_is_as_before()
    {
        var withoutIndex = Row(ImageItem(Behind()), null);
        var emptyIndex = Row(ImageItem(Behind()), InfrastructureIndex.Empty);

        withoutIndex.Why.Should().Be(CeilingReason.NoActionDefined);
        emptyIndex.Why.Should().Be(CeilingReason.NoActionDefined);
        withoutIndex.Locations.Should().BeNull();
    }

    [Fact]
    public void A_line_that_Flux_rewrites_is_shown_and_left_alone()
    {
        var row = Row(ImageItem(Behind()), Code("image: n8nio/n8n:1.108.2 # {\"$imagepolicy\": \"flux-system:n8n\"}\n"));

        row.Why.Should().Be(CeilingReason.ManagedByAutomation);
        row.ProposedTag.Should().BeNull();
        row.Locations.Should().ContainSingle();
    }

    [Fact]
    public void When_the_code_says_another_version_than_the_one_that_runs_the_row_says_so_instead_of_proposing()
    {
        var row = Row(ImageItem(Behind()), Code("image: n8nio/n8n:1.110.0\n"));

        row.Why.Should().Be(CeilingReason.CodeDiffersFromRunning);
        row.ProposedTag.Should().BeNull();
    }

    [Fact]
    public void A_major_version_shows_where_but_is_a_change_to_plan_and_not_a_tag_to_write()
    {
        var row = Row(ImageItem(Behind(latest: "2.0.0", change: ImageChange.Major)), Code("image: n8nio/n8n:1.108.2\n"));

        row.Why.Should().Be(CeilingReason.MajorNeedsPlan);
        row.ProposedTag.Should().BeNull();
        row.Locations.Should().ContainSingle();
    }

    [Fact]
    public void A_floating_image_is_pinned_to_the_version_that_runs_when_it_is_known()
    {
        var floating = new Finding(FindingKind.FloatingImage, Severity.Info, 1, Image: new ImageUpdate("pihole/pihole:latest", "2025.07.1", null, ImageChange.None));
        var unknown = new Finding(FindingKind.FloatingImage, Severity.Info, 1, Image: new ImageUpdate("pihole/pihole:latest", null, null, ImageChange.None));
        var code = Code("image: pihole/pihole:latest\n");

        var pinned = Row(ImageItem(floating), code);
        var cannot = Row(ImageItem(unknown), code);

        pinned.Why.Should().Be(CeilingReason.CodeChangeShown);
        pinned.ProposedTag.Should().Be("2025.07.1");
        cannot.ProposedTag.Should().BeNull("the version that runs is not known: nothing is guessed");
        cannot.Locations.Should().ContainSingle();
    }

    [Fact]
    public void An_image_of_the_platform_stays_the_platforms_whatever_the_code_says()
    {
        var row = Row(ImageItem(Behind("rancher/mirrored-coredns-coredns:1.14.7", "1.14.7", "1.15.0"), space: "kube-system"), Code("image: rancher/mirrored-coredns-coredns:1.14.7\n"));

        row.Why.Should().Be(CeilingReason.ManagedByPlatform);
        row.Locations.Should().BeNull();
    }

    [Fact]
    public void An_image_written_at_two_versions_is_about_the_places_that_write_the_version_that_runs()
    {
        var code = InfrastructureIndex.Build(new InfrastructureSnapshot(DateTimeOffset.UnixEpoch,
            [new InfrastructureFile("example/infra", "k8s/a.yaml", "kubernetes", "image: postgres:15-alpine\n"), new InfrastructureFile("example/infra", "k8s/b.yaml", "kubernetes", "image: postgres:16-alpine\n")], []));

        var row = Row(ImageItem(Behind("postgres:16-alpine", "16-alpine", "17-alpine")), code);

        row.Why.Should().Be(CeilingReason.CodeChangeShown);
        row.Locations.Should().ContainSingle().Which.Path.Should().Be("k8s/b.yaml");
    }

    [Fact]
    public void A_floating_tag_that_is_a_branch_is_not_pinned_to_itself()
    {
        var floating = new Finding(FindingKind.FloatingImage, Severity.Info, 1, Image: new ImageUpdate("ghcr.io/example/app:develop", "develop", null, ImageChange.None));

        var row = Row(ImageItem(floating), Code("image: ghcr.io/example/app:develop\n"));

        row.ProposedTag.Should().BeNull();
        row.Locations.Should().ContainSingle();
    }

    private static InfrastructureIndex CodeIn(string environment, string manifest) =>
        InfrastructureIndex.Build(new InfrastructureSnapshot(DateTimeOffset.UnixEpoch, [new InfrastructureFile("example/infra", "k8s/n8n.yaml", "kubernetes", manifest, environment)], []));

    [Fact]
    public void A_repository_that_deploys_to_one_environment_is_about_the_workloads_of_that_environment_only()
    {
        var item = ImageItem(Behind()) with { Placements = [new Placement("host-1", "Host 1", null, "n8n")] };

        var elsewhere = Row(item, CodeIn("k3s", "image: n8nio/n8n:1.108.2\n"));
        var there = Row(item, CodeIn("host-1", "image: n8nio/n8n:1.108.2\n"));

        elsewhere.Why.Should().Be(CeilingReason.NotInScope, "the code of another environment does not describe this workload");
        there.Why.Should().Be(CeilingReason.CodeChangeShown);
    }

    [Fact]
    public void A_repository_without_an_environment_is_about_every_workload()
    {
        var item = ImageItem(Behind()) with { Placements = [new Placement("host-1", "Host 1", null, "n8n")] };

        Row(item, Code("image: n8nio/n8n:1.108.2\n")).Why.Should().Be(CeilingReason.CodeChangeShown);
    }

    [Fact]
    public void When_no_place_writes_the_version_that_runs_the_row_says_the_code_differs_whatever_the_number_of_places()
    {
        var row = Row(ImageItem(Behind()), Code("image: n8nio/n8n:1.110.0\nimage: n8nio/n8n:1.111.0\n"));

        row.Why.Should().Be(CeilingReason.CodeDiffersFromRunning);
        row.Locations.Should().HaveCount(2);
    }

    [Fact]
    public void An_image_that_runs_in_several_environments_shows_its_places_and_proposes_no_change_for_all_at_once()
    {
        var item = ImageItem(Behind()) with { Placements = [new Placement("staging", "Staging", null, "n8n"), new Placement("production", "Production", null, "n8n")] };

        var row = Row(item, Code("image: n8nio/n8n:1.108.2\n"));

        row.Why.Should().Be(CeilingReason.SeveralEnvironments);
        row.ProposedTag.Should().BeNull();
        row.Locations.Should().ContainSingle();
    }

    [Fact]
    public void A_proposed_tag_that_is_not_a_tag_is_not_proposed()
    {
        var row = Row(ImageItem(Behind(latest: "1.121.0\"#")), Code("image: n8nio/n8n:1.108.2\n"));

        row.ProposedTag.Should().BeNull();
        row.Why.Should().NotBe(CeilingReason.CodeChangeShown);
    }
}
