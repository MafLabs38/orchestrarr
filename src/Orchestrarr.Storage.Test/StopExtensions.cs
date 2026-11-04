using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Storage.Test;

/// <summary>Puts a stop in a given state the way the product does, through <see cref="IKillSwitchControl.ChangeAsync"/>, and says what became of it.</summary>
internal static class StopExtensions
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    public static KillSwitchChange Engage(this IKillSwitchControl stop) => Change(stop, engaged: true);

    public static KillSwitchChange Release(this IKillSwitchControl stop) => Change(stop, engaged: false);

    private static KillSwitchChange Change(IKillSwitchControl stop, bool engaged)
    {
        var entry = new AuditEntry(Noon, engaged ? AuditEvents.KillSwitchEngaged : AuditEvents.KillSwitchReleased, "alice-1", null, null);
        return stop.ChangeAsync(engaged, entry, TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult();
    }
}
