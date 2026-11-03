using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Approvals;

/// <summary>
/// The stop in memory, the sandbox's: a restart releases it. The persistent one counts unknown as engaged (ADR 0013). With no trail given, a
/// change has nothing to trace to and counts as traced.
/// </summary>
public sealed class InMemoryKillSwitch(IAuditSink? audit = null) : IKillSwitchControl, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _engaged;

    public bool Engaged => _engaged;

    public bool Saved => true;

    /// <summary>Changes the stop without tracing it: for what is not a decision of a person (tests, the sandbox's start).</summary>
    public void Set(bool engaged) => _engaged = engaged;

    public ValueTask<bool> IsEngagedAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_engaged);

    public async ValueTask<KillSwitchChange> ChangeAsync(bool engaged, AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var traced = audit is null || await audit.TryRecordAsync(entry, cancellationToken);
            if (engaged)
            {
                _engaged = true;
                return traced ? KillSwitchChange.Applied : KillSwitchChange.AppliedUntraced;
            }
            if (!traced)
            {
                return KillSwitchChange.NotTraced;
            }
            _engaged = false;
            return KillSwitchChange.Applied;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
