namespace Orchestrarr.Core.Approvals;

/// <summary>
/// The one rule of a bounded audit trail, shared by every store so that they cannot drift: it refuses when it is full, so that nothing happens
/// without a trace, and refusals have only a share of it, so that a client that is refused again and again cannot use it up and make every real
/// decision (and every stop) untraceable.
/// </summary>
public static class AuditCapacityPolicy
{
    /// <summary>The share of the trail that refusals may use, in quarters.</summary>
    private const int RefusalQuarters = 3;

    /// <summary>How many refusals a trail of this capacity may hold.</summary>
    public static int RefusalLimit(int capacity) => (int)(capacity * (long)RefusalQuarters / 4);

    /// <summary>Whether these entries fit, given how many the trail holds and how many of them are refusals.</summary>
    public static bool Fits(int capacity, int held, int refusalsHeld, int adding, int refusalsAdding) =>
        held + adding <= capacity && (refusalsAdding == 0 || refusalsHeld + refusalsAdding <= RefusalLimit(capacity));
}
