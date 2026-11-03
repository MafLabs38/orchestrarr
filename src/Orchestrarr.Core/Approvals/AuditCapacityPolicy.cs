using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Approvals;

/// <summary>
/// The one rule of a bounded audit trail, shared by every store so that they cannot drift: it refuses when it is full, so that nothing happens
/// without a trace; refusals have only a share of it, so that a client that is refused again and again cannot use it up; and the last part of it
/// is a reserve for the global stop, so that a trail filled by decisions can still trace the stop and its release.
/// </summary>
public static class AuditCapacityPolicy
{
    /// <summary>The share of the trail that refusals may use, in quarters.</summary>
    private const int RefusalQuarters = 3;

    /// <summary>The share of the trail kept for the global stop, in twentieths (5 %).</summary>
    private const int ReserveTwentieths = 1;

    /// <summary>How many refusals a trail of this capacity may hold.</summary>
    public static int RefusalLimit(int capacity) => (int)(capacity * (long)RefusalQuarters / 4);

    /// <summary>How many entries only the global stop (and the creation of the database) may use: at least one, never more than the trail.</summary>
    public static int Reserve(int capacity) => Math.Min(capacity, Math.Max(1, (int)(capacity * (long)ReserveTwentieths / 20)));

    /// <summary>Whether these entries fit, given how many the trail holds and how many of them are refusals.</summary>
    public static bool Fits(int capacity, int held, int refusalsHeld, IReadOnlyList<AuditEntry> adding)
    {
        ArgumentNullException.ThrowIfNull(adding);
        var refusals = adding.Count(entry => AuditEvents.IsRefusal(entry.Event));
        var ceiling = adding.All(entry => AuditEvents.UsesReserve(entry.Event)) ? capacity : capacity - Reserve(capacity);
        return held + adding.Count <= ceiling && (refusals == 0 || refusalsHeld + refusals <= RefusalLimit(capacity));
    }
}
