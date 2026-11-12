using Orchestrarr.Core.Infrastructure;

namespace Orchestrarr.Core.Test.Infrastructure;

public sealed class ScopeChecksTests
{
    private static readonly DescriptionScope Scope = new(
        [new EnvironmentScope("k3s", null), new EnvironmentScope("prod", null)],
        [new PlatformScope("k3s", "k3s", "k3s", null)],
        [new RepositoryScope("example/infra", "kubernetes", ["a/**"], "staging")]);

    [Fact]
    public void An_environment_that_no_workload_of_the_fleet_is_in_is_told_whatever_the_place_it_is_named_in()
    {
        var problems = ScopeChecks.UnknownEnvironments(Scope, ["k3s", "production"]);

        problems.Select(problem => problem.Subject).Should().Equal("prod", "staging");
    }

    [Fact]
    public void Nothing_is_said_when_every_environment_is_known_or_when_the_fleet_has_not_been_read()
    {
        ScopeChecks.UnknownEnvironments(Scope, ["k3s", "prod", "staging"]).Should().BeEmpty();
        ScopeChecks.UnknownEnvironments(Scope, []).Should().BeEmpty();
    }
}
