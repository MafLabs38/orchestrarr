using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Approvals;

/// <summary>
/// The stop in memory. A restart releases it, which the contract of <see cref="IKillSwitch"/> ("unknown means engaged") forbids once
/// something can run: a persistent switch that starts engaged comes with the launcher (ADR 0010).
/// </summary>
public sealed class InMemoryKillSwitch : IKillSwitchControl
{
    private volatile bool _engaged;

    public bool Engaged => _engaged;

    public void Set(bool engaged) => _engaged = engaged;

    public ValueTask<bool> IsEngagedAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_engaged);
}
