using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

public sealed class LeadPlannerTests
{
    private sealed class CountingGateway : IAgentGateway
    {
        private readonly object _gate = new();
        private int _running;
        public int MostAtOnce { get; private set; }

        public async Task<AgentReply> RunAsync(AgentRequest request, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _running);
            lock (_gate)
            {
                MostAtOnce = Math.Max(MostAtOnce, now);
            }
            await Task.Delay(40, cancellationToken);
            Interlocked.Decrement(ref _running);
            return new AgentReply(true, "{\"steps\":[{\"world\":\"staging\",\"instruction\":\"Test.\"}],\"reasons\":[\"r\"]}", null);
        }
    }

    [Fact]
    public async Task The_lead_is_asked_two_questions_at_a_time_at_most_whatever_the_size_of_the_selection()
    {
        var gateway = new CountingGateway();
        var planner = new LeadPlanner(new ControllerAgent(gateway, "m", TimeSpan.FromMinutes(1)));
        var input = new LeadInput(new ProblemBrief("DependencyUpdate", "example/shop", "x 1 → 2", true), World.Release, World.Staging);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => planner.PlanAsync(input, TestContext.Current.CancellationToken)));

        outcomes.Should().OnlyContain(outcome => outcome.Plan != null);
        gateway.MostAtOnce.Should().BeInRange(1, 2);
    }
}
