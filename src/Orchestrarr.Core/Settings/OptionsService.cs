using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Core.Settings;

/// <summary>
/// The options and the rule that keeps them safe (ADR 0023). Turning off an option that lowers security needs the host to have been started
/// with leave to do so; without it the stored value is not even read, so a value written by other means changes nothing. An unreadable store reads as the
/// safe value. Every change is traced before it is made.
/// </summary>
public sealed class OptionsService(IOptionStore store, IAuditSink audit, TimeProvider time, OptionsSettings settings, TaskOptions tasks) : ISecondFactorPolicy
{
    public const string Changed = "option.changed";

    public bool Required => tasks.RequireSecondFactor && !(settings.AllowRelaxingSecurity && StoredValue(OptionIds.RequireSecondFactor) == false);

    public IReadOnlyList<OptionState> List() => [State(OptionIds.RequireSecondFactor)];

    public async Task<OptionRefusal?> SetAsync(string id, bool value, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (!OptionIds.All.Contains(id, StringComparer.Ordinal))
        {
            return OptionRefusal.UnknownOption;
        }
        if (!tasks.RequireSecondFactor)
        {
            return OptionRefusal.SetByConfiguration;
        }
        // Turning on is always allowed; turning off needs the leave of the host
        if (!value && !settings.AllowRelaxingSecurity)
        {
            return OptionRefusal.LockedByHost;
        }

        var now = time.GetUtcNow();
        // The trace first: a relaxation nobody can see is not made
        if (!await audit.TryRecordAsync(new AuditEntry(now, Changed, actor, null, $"{id}: {(value ? "on" : "off")}"), cancellationToken))
        {
            return OptionRefusal.Unavailable;
        }
        return store.TrySet(id, value, now, actor) ? null : OptionRefusal.Unavailable;
    }

    private OptionState State(string id)
    {
        var fixedByConfiguration = !tasks.RequireSecondFactor;
        string? reason = null;
        if (fixedByConfiguration)
        {
            reason = "configuration";
        }
        else if (!settings.AllowRelaxingSecurity)
        {
            reason = "host";
        }
        return new OptionState(id, Required, Default: true, LowersSecurityWhenOff: true, CanTurnOff: reason is null, reason);
    }

    // An unreadable store is the safe value: the option stays on
    private bool? StoredValue(string id)
    {
        try
        {
            return store.Get(id);
        }
        catch (StorageUnavailableException)
        {
            return null;
        }
    }
}
