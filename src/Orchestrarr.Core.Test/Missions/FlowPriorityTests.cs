using Orchestrarr.Core.Missions;

namespace Orchestrarr.Core.Test.Missions;

public sealed class FlowPriorityTests
{
    [Theory]
    [InlineData(0, 70)]
    [InlineData(50, 70)]
    [InlineData(69, 70)]
    [InlineData(70, 71)]
    [InlineData(99, 100)]
    [InlineData(100, 100)]
    [InlineData(1000, 100)]
    public void An_urgent_event_starts_at_seventy_then_adds_one_and_stops_at_a_hundred(int current, int expected) =>
        FlowPriority.Raised(current).Should().Be(expected);

    [Fact]
    public void A_problem_is_normal_unless_something_says_otherwise() =>
        FlowPriority.Normal.Should().Be(50);
}
