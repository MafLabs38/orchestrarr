using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

public sealed class SandboxGrantsTests
{
    [Fact]
    public void Every_application_has_the_highest_world_unless_it_has_a_lower_limit_of_its_own()
    {
        var grants = new SandboxGrants(World.Release, new Dictionary<string, World> { ["edge-stack"] = World.Git, ["shop-api"] = World.Release });

        grants.MaxWorldFor("edge-stack").Should().Be(World.Git);
        grants.MaxWorldFor("shop-api").Should().Be(World.Release);
        grants.MaxWorldFor("ledger-worker").Should().Be(World.Release);
    }

    [Fact]
    public void A_limit_never_raises_the_highest_world()
    {
        new SandboxGrants(World.Git, new Dictionary<string, World> { ["edge-stack"] = World.Release }).MaxWorldFor("edge-stack").Should().Be(World.Git);
    }
}
