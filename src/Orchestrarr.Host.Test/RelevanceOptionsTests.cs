using Orchestrarr.Core.Decisions;
using Orchestrarr.Host.Decisions;

namespace Orchestrarr.Host.Test;

/// <summary>The <c>Relevance</c> section: the defaults of the policy, a list that replaces the default one, and a delay that makes no sense stops the host.</summary>
public sealed class RelevanceOptionsTests
{
    [Fact]
    public void Nothing_set_gives_the_default_policy()
    {
        var policy = new RelevanceOptions().ToPolicy();

        policy.DormantAfterDays.Should().Be(RelevancePolicy.Default.DormantAfterDays);
        policy.PlatformNamespaces.Should().Equal(RelevancePolicy.Default.PlatformNamespaces);
        policy.EvenMajorOnlyPackages.Should().Equal(RelevancePolicy.Default.EvenMajorOnlyPackages);
    }

    [Fact]
    public void A_list_given_replaces_the_default_one()
    {
        var policy = new RelevanceOptions { DormantAfterDays = 90, PlatformNamespaces = ["system"], EvenMajorOnlyPackages = [] }.ToPolicy();

        policy.DormantAfterDays.Should().Be(90);
        policy.PlatformNamespaces.Should().Equal("system");
        policy.EvenMajorOnlyPackages.Should().BeEmpty();
    }

    [Theory]
    [InlineData(7)]
    [InlineData(3650)]
    public void The_edges_of_the_delay_are_accepted(int days) => new RelevanceOptions { DormantAfterDays = days }.ToPolicy().DormantAfterDays.Should().Be(days);

    [Theory]
    [InlineData(6)]
    [InlineData(3651)]
    [InlineData(-1)]
    public void A_delay_outside_seven_days_to_ten_years_is_refused(int days)
    {
        var act = () => new RelevanceOptions { DormantAfterDays = days }.ToPolicy();

        act.Should().Throw<InvalidOperationException>().WithMessage("*DormantAfterDays*");
    }
}
