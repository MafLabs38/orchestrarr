using Orchestrarr.Core.Missions;

namespace Orchestrarr.Host.Missions;

/// <summary>
/// Asks the lead agent for the plan of a problem (ADR 0046). The lead thinks and has no power: what it proposes is checked by the conductor before anything starts. The number of questions at a time is bounded (`Agents:LeadMaxConcurrent`): the lead is a
/// run of the subscription.
/// </summary>
internal sealed class LeadPlanner(ControllerAgent agent, int maxConcurrent = 2)
{
    private readonly SemaphoreSlim _slots = new(Math.Max(1, maxConcurrent), Math.Max(1, maxConcurrent));

    public async Task<LeadOutcome> PlanAsync(LeadInput input, CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);
        try
        {
            return await agent.PlanAsync(input, cancellationToken);
        }
        finally
        {
            _slots.Release();
        }
    }
}
