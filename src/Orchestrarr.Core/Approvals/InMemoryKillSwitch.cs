using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Approvals;

/// <summary>
/// The stop in memory, the sandbox's: a restart releases it. The persistent one counts unknown as engaged (ADR 0013). With no trail given, a
/// change has nothing to trace to and counts as traced.
/// </summary>
public sealed class InMemoryKillSwitch(IAuditSink? audit = null, bool startEngaged = false) : IKillSwitchControl, IDisposable
{
    // Shared with the in-memory ledger: it checks the stop and writes while it holds this, and an engagement traces while it holds it, so the
    // two are one after the other. An engagement counts before it waits for it, so that an approval that comes after it is refused.
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    private volatile bool _engaged = startEngaged;

    // Engagements waiting for the gate: they count from the moment they are asked for, and a release that finishes first cannot undo them
    private int _pending;

    public bool Engaged => _engaged || Volatile.Read(ref _pending) > 0;

    // Nothing here survives a restart, and the state says so
    public KillSwitchState State() => new(Engaged, Saved: false);

    public ValueTask<bool> IsEngagedAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Engaged);

    public async ValueTask<KillSwitchChange> ChangeAsync(bool engaged, AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // Engaging always takes effect, and a client that gave up is no reason to skip it
        var token = engaged ? CancellationToken.None : cancellationToken;
        if (engaged)
        {
            Interlocked.Increment(ref _pending);
        }
        try
        {
            await Gate.WaitAsync(token);
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
                Gate.Release();
            }
        }
        finally
        {
            if (engaged)
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    public void Dispose() => Gate.Dispose();
}
