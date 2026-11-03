using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Approvals;

/// <summary>
/// The stop in memory, the sandbox's: a restart releases it. The persistent one counts unknown as engaged (ADR 0013). With no trail given, a
/// change has nothing to trace to and counts as traced.
/// </summary>
public sealed class InMemoryKillSwitch(IAuditSink? audit = null, bool startEngaged = false) : IKillSwitchControl, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _engaged = startEngaged;

    public bool Engaged => _engaged;

    // Nothing here survives a restart, and the state says so
    public KillSwitchState State() => new(_engaged, Saved: false);

    public ValueTask<bool> IsEngagedAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_engaged);

    public async ValueTask<KillSwitchChange> ChangeAsync(bool engaged, AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // Engaging always takes effect, and a client that gave up is no reason to skip it
        var token = engaged ? CancellationToken.None : cancellationToken;
        await _gate.WaitAsync(token);
        try
        {
            var traced = audit is null || await audit.TryRecordAsync(entry, token);
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
