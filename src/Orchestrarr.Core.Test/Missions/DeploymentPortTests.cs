using Orchestrarr.Core.Missions;

namespace Orchestrarr.Core.Test.Missions;

public sealed class DeploymentPortTests
{
    [Fact]
    public void A_repository_always_lands_on_the_same_port_within_the_span_above_the_base()
    {
        var port = DeploymentPort.Of("example/shop", 8100);

        port.Should().Be(DeploymentPort.Of("example/shop", 8100));
        port.Should().BeInRange(8100, 8100 + DeploymentPort.Span - 1);
        DeploymentPort.Of("example/shop", 9000).Should().Be(port + 900, "the base moves every port by the same amount");
    }

    [Fact]
    public void Repositories_do_not_all_land_on_one_port()
    {
        Enumerable.Range(0, 20).Select(n => DeploymentPort.Of($"example/app-{n}", 8100)).Distinct().Count().Should().BeGreaterThan(5);
    }
}
