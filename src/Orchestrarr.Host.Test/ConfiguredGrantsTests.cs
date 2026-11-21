using Microsoft.Extensions.Configuration;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

public sealed class ConfiguredGrantsTests
{
    private static ConfiguredGrants Grants(params (string Application, string World)[] grants) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(grants.ToDictionary(grant => $"Missions:Grants:{grant.Application}", grant => (string?)grant.World)).Build());

    [Fact]
    public void An_application_that_is_not_listed_has_no_power_and_a_listed_one_has_the_world_the_maintainer_wrote()
    {
        var grants = Grants(("shop", "Git"), ("blog", "release"));

        grants.MaxWorldFor("shop").Should().Be(World.Git);
        grants.MaxWorldFor("blog").Should().Be(World.Release, "the name of a world is read whatever its case");
        grants.MaxWorldFor("other").Should().BeNull("a grant is off until the maintainer enables it (D-10)");
    }

    [Fact]
    public void An_application_id_is_compared_as_written_and_one_with_the_path_separator_names_nothing()
    {
        var grants = Grants(("Shop", "Release"), ("a", "Git"));

        grants.MaxWorldFor("shop").Should().BeNull("Shop and shop are two applications");
        grants.MaxWorldFor("a:b").Should().BeNull("a colon would reach into another section of the configuration");
        grants.MaxWorldFor("Shop").Should().Be(World.Release);
    }

    [Theory]
    [InlineData("Everything")]
    [InlineData("")]
    [InlineData("7")]
    public void A_grant_that_is_not_the_name_of_a_world_is_no_power(string value)
    {
        Grants(("shop", value)).MaxWorldFor("shop").Should().BeNull();
    }
}
