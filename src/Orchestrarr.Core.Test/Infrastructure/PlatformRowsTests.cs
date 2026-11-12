using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Infrastructure;

namespace Orchestrarr.Core.Test.Infrastructure;

public sealed class PlatformRowsTests
{
    private static readonly PlatformScope K3s = new("k3s", "k3s", "k3s", new VersionPointer("example/cluster", "ansible/all.yml", "k3s_version"));

    private static InfrastructureIndex Code(string variables) =>
        InfrastructureIndex.Build(new InfrastructureSnapshot(DateTimeOffset.UnixEpoch, [new InfrastructureFile("example/cluster", "ansible/all.yml", "variables", variables)], []));

    private static K3sReleases Releases(params string[] latest) =>
        new(K3sVersion.Parse(latest[^1])!, latest.Select(version => K3sVersion.Parse(version)!).ToDictionary(version => version.Minor));

    [Theory]
    [InlineData("v1.36.5+k3s1", "v1.36.6+k3s1", -1)]
    [InlineData("v1.36.5+k3s1", "v1.36.5+k3s2", -1)]
    [InlineData("v1.36.5+k3s1", "v1.37.0+k3s1", -1)]
    [InlineData("v1.36.5+k3s1", "v1.36.5+k3s1", 0)]
    [InlineData("v1.36.10+k3s1", "v1.36.9+k3s1", 1)]
    public void K3s_versions_are_compared_by_kubernetes_first_and_the_build_second(string left, string right, int expected) =>
        Math.Sign(K3sVersion.Parse(left)!.CompareTo(K3sVersion.Parse(right))).Should().Be(expected);

    [Theory]
    [InlineData("1.36.5")]
    [InlineData("v1.36.5")]
    [InlineData("stable")]
    public void What_is_not_a_k3s_version_is_not_read(string text) => K3sVersion.Parse(text).Should().BeNull();

    [Fact]
    public void A_patch_of_the_same_minor_and_the_next_minor_only_are_told_with_the_place_of_the_variable()
    {
        var rows = PlatformRows.Build([K3s], Code("---\nk3s_version: \"v1.36.5+k3s1\"\n"), Releases("v1.35.10+k3s1", "v1.36.6+k3s1", "v1.37.2+k3s1", "v1.38.1+k3s1"));

        rows.Select(row => (row.Id, row.Description)).Should().Equal(("platform:k3s:patch", "v1.36.5+k3s1 → v1.36.6+k3s1"), ("platform:k3s:minor", "v1.36.5+k3s1 → v1.37.2+k3s1"));
        rows.Should().OnlyContain(row => row.ProposedTag == null, "a platform upgrade is told and described, no change is computed for it");
        rows.Should().OnlyContain(row => row.Why == CeilingReason.PlatformUpgradeIsPlanned && row.Kind == ProblemKind.PlatformUpdate);
        rows[0].Locations!.Single().Should().Match<CodeLocation>(location => location.Line == 2 && location.Form == VersionForm.Variable && location.Path == "ansible/all.yml");
    }

    [Theory]
    [InlineData("v1.99999999999.0+k3s1")]
    [InlineData("v١.٣٦.٥+k3s1")]
    [InlineData("v1.36.5+k3s99999999")]
    public void A_value_in_a_file_that_is_not_a_k3s_version_is_not_read_and_never_throws(string written)
    {
        var rows = PlatformRows.Build([K3s], Code($"k3s_version: {written}\n"), Releases("v1.36.6+k3s1"));

        rows.Should().BeEmpty();
    }

    [Fact]
    public void A_cluster_up_to_date_has_no_row_and_nothing_is_said_without_the_releases()
    {
        PlatformRows.Build([K3s], Code("k3s_version: v1.36.6+k3s1\n"), Releases("v1.36.6+k3s1")).Should().BeEmpty();
        PlatformRows.Build([K3s], Code("k3s_version: v1.36.5+k3s1\n"), null).Should().BeEmpty();
    }

    [Fact]
    public void A_variable_that_is_not_where_the_description_says_is_told_when_the_code_was_read_and_only_then()
    {
        var told = PlatformRows.Build([K3s], Code("other: 1\n"), Releases("v1.36.6+k3s1"));

        told.Should().ContainSingle().Which.Should().Match<ProblemRow>(row => row.Why == CeilingReason.PlatformVersionNotRead && row.Description.Contains("k3s_version", StringComparison.Ordinal));
        PlatformRows.Build([K3s], InfrastructureIndex.Empty, Releases("v1.36.6+k3s1")).Should().BeEmpty("without a scope nothing is said");
    }

    [Fact]
    public void A_repository_that_could_not_be_read_does_not_make_the_variable_missing()
    {
        var unreadable = InfrastructureIndex.Build(new InfrastructureSnapshot(DateTimeOffset.UnixEpoch, [], [new RepositoryProblem("example/cluster", "not found or no access")]));

        PlatformRows.Build([K3s], unreadable, Releases("v1.36.6+k3s1")).Should().BeEmpty();
    }
}
