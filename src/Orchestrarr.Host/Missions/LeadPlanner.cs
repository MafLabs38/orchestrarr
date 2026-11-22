using Orchestrarr.Core.Missions;

namespace Orchestrarr.Host.Missions;

/// <summary>
/// Asks the lead agent for the plan of a problem (ADR 0046). The lead thinks and has no power: what it proposes is checked by the conductor before anything starts. Two questions at a time at most: the lead is a
/// process of the subscription, and the machine is small.
/// </summary>
internal sealed class LeadPlanner(ControllerAgent agent)
{
    private readonly SemaphoreSlim _slots = new(2, 2);

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
