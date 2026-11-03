using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Approvals;

/// <summary>The stop in memory, the sandbox's: a restart releases it. <c>SqliteKillSwitch</c> is the persistent one, which counts unknown as engaged (ADR 0013).</summary>
public sealed class InMemoryKillSwitch : IKillSwitchControl
{
    private volatile bool _engaged;

    public bool Engaged => _engaged;

    public bool Saved => true;

    public void Set(bool engaged) => _engaged = engaged;

    public ValueTask<bool> IsEngagedAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_engaged);
}
