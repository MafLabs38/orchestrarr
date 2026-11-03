using Orchestrarr.Core.Forge;

namespace Orchestrarr.Core.Test;

public sealed class DependencyTitleTests
{
    [Theory]
    [InlineData("chore(deps): Bump lodash from 4.17.4 to 4.17.21", "lodash", "4.17.4", "4.17.21", UpdateKind.Patch)]
    [InlineData("chore(deps-dev): Bump mocha from 8.0.1 to 12.0.3", "mocha", "8.0.1", "12.0.3", UpdateKind.Major)]
    [InlineData("Bump express from 4.16.0 to 4.21.2 in /app", "express", "4.16.0", "4.21.2", UpdateKind.Minor)]
    [InlineData("chore(deps): Bump actions/checkout from 2 to 7", "actions/checkout", "2", "7", UpdateKind.Major)]
    [InlineData("chore(deps): Bump node from 14-alpine to 26-alpine", "node", "14-alpine", "26-alpine", UpdateKind.Major)]
    [InlineData("build(deps): bump Newtonsoft.Json from v12.0.1 to v13.0.3", "Newtonsoft.Json", "v12.0.1", "v13.0.3", UpdateKind.Major)]
    public void A_pull_request_that_updates_one_dependency_says_which_and_how_far(string title, string package, string from, string to, UpdateKind kind)
    {
        DependencyTitle.Parse(title).Should().ContainSingle().Which.Should().Be(new DependencyChange(package, from, to, kind));
    }

    [Theory]
    [InlineData("chore(deps): Bump qs and express", "qs", "express")]
    [InlineData("chore(deps): Bump a, b and c", "a", "b", "c")]
    public void A_pull_request_that_updates_several_dependencies_names_them_without_versions(string title, params string[] packages)
    {
        var changes = DependencyTitle.Parse(title);

        changes.Select(c => c.Package).Should().Equal(packages);
        changes.Should().OnlyContain(c => c.From == null && c.To == null && c.Kind == UpdateKind.Unknown);
    }

    [Theory]
    [InlineData("chore(deps): Bump the npm_and_yarn group with 3 updates")]
    [InlineData("Bump the nuget group across 1 directory with 2 updates")]
    public void A_grouped_update_is_one_change_named_after_its_group(string title)
    {
        var changes = DependencyTitle.Parse(title);

        changes.Should().ContainSingle().Which.Package.Should().EndWith(" group").And.NotContain("updates");
        changes[0].From.Should().BeNull();
    }

    [Theory]
    [InlineData("Update dependency lodash to v4.17.21", "lodash", "v4.17.21")]
    [InlineData("chore(deps): update dependency lodash to v4.17.21", "lodash", "v4.17.21")]
    [InlineData("Update actions/checkout action to v4", "actions/checkout", "v4")]
    [InlineData("Update node docker tag to v22", "node", "v22")]
    [InlineData("chore(deps): update lodash to v5", "lodash", "v5")]
    public void A_renovate_pull_request_names_the_package_and_where_it_goes_but_not_how_far(string title, string package, string to)
    {
        DependencyTitle.Parse(title).Should().ContainSingle().Which.Should().Be(new DependencyChange(package, null, to, UpdateKind.Unknown));
    }

    [Fact]
    public void A_renovate_pull_request_with_no_target_names_the_package_only()
    {
        DependencyTitle.Parse("Update dependency lodash").Should().ContainSingle().Which.Should().Be(new DependencyChange("lodash", null, null, UpdateKind.Unknown));
    }

    [Fact]
    public void A_very_long_hostile_title_is_read_in_bounded_time_and_gives_something_or_nothing()
    {
        var title = "Bump " + new string('a', 200_000) + " and " + new string('b', 200_000);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var changes = DependencyTitle.Parse(title);
        watch.Stop();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        changes.Should().NotBeNull();
    }

    [Theory]
    [InlineData("Add a feature")]
    [InlineData("")]
    public void A_title_that_is_not_a_bump_updates_nothing_we_know_of(string title)
    {
        DependencyTitle.Parse(title).Should().BeEmpty();
    }

    [Theory]
    [InlineData("1.2.3", "1.2.4", UpdateKind.Patch)]
    [InlineData("1.2.3", "1.3.0", UpdateKind.Minor)]
    [InlineData("1.2.3", "2.0.0", UpdateKind.Major)]
    [InlineData("1.2.3", "1.2.3", UpdateKind.Unknown)]
    [InlineData("latest", "1.2.3", UpdateKind.Unknown)]
    [InlineData("1.0.0-beta.1", "1.0.0", UpdateKind.Unknown)]
    [InlineData("2", "2.1", UpdateKind.Minor)]
    public void The_kind_of_an_update_comes_from_the_first_numbers_that_differ(string from, string to, UpdateKind expected)
    {
        DependencyTitle.Kind(from, to).Should().Be(expected);
    }
}
