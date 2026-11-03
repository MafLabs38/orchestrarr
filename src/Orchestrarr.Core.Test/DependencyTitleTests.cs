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

    [Fact]
    public void A_grouped_update_is_one_change_named_after_its_group()
    {
        var changes = DependencyTitle.Parse("chore(deps): Bump the npm_and_yarn group with 3 updates");

        changes.Should().ContainSingle().Which.Package.Should().StartWith("the npm_and_yarn group");
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

    [Fact]
    public void A_null_title_is_refused()
    {
        var parse = () => DependencyTitle.Parse(null!);

        parse.Should().Throw<ArgumentNullException>();
    }
}
