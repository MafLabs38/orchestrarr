using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.WorkItems;

/// <summary>
/// The launcher's second check of the powers (SPEC D-47), reading the same grants as the decision card: one source, so that what a card
/// offers and what the launcher accepts cannot drift apart.
/// </summary>
public sealed class PolicyGrantedPowers(IPowerPolicy policy) : IGrantedPowers
{
    public ValueTask<bool> AllowsAsync(LaunchOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        return ValueTask.FromResult(order.Steps.Count > 0 && order.Steps.All(step => policy.IsGranted(step, order.Repository, order.Environment)));
    }
}
