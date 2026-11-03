using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Approvals;

/// <summary>What became of a change of the global stop.</summary>
public enum KillSwitchChange
{
    /// <summary>The stop is as asked, and the change is in the trail.</summary>
    Applied,

    /// <summary>The stop is engaged, but its trace could not be written (the trail is full or unwritable): engaging never waits for it.</summary>
    AppliedUntraced,

    /// <summary>A release could not be saved: the stop stays engaged.</summary>
    NotSaved,

    /// <summary>A release could not be traced: the stop stays engaged, because a release nobody can see is a stop undone in silence.</summary>
    NotTraced,
}

/// <summary>The stop as it is read at one moment: whether it is engaged, and whether that is kept (false: held only in memory, or unknown because the file cannot be read).</summary>
public readonly record struct KillSwitchState(bool Engaged, bool Saved);

/// <summary>The global stop, with the way to change it (SPEC D-43). Engaged, nothing is approved and nothing launches.</summary>
public interface IKillSwitchControl : IKillSwitch
{
    bool Engaged { get; }

    /// <summary>Both answers from one read: what the status and the responses of the stop say, so that the pair never comes from two moments.</summary>
    KillSwitchState State();

    /// <summary>False while an engagement is held only in memory because it could not be saved: it would not survive a restart.</summary>
    bool Saved { get; }

    /// <summary>
    /// Engages or releases the stop, and traces it with <paramref name="entry"/>. Engaging always takes effect, even when it cannot be saved or
    /// traced, because the stop must work when nothing else does. A release needs to be saved and traced, in one write where the storage allows
    /// it: otherwise the stop stays engaged (unknown means engaged). The implementations are synchronous: a call can wait for the file, no longer than
    /// its busy timeout, and an engagement does not wait for anything else.
    /// </summary>
    ValueTask<KillSwitchChange> ChangeAsync(bool engaged, AuditEntry entry, CancellationToken cancellationToken);
}
