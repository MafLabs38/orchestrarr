using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Approvals;

/// <summary>
/// The audit trail in memory and bounded: the sandbox's store, not durable. Past its capacity it refuses, so that nothing happens without a
/// trace; the persistent trail that SPEC D-43 and F-SAF-02 require comes with the workshop. Refusals have a smaller share than the rest: a
/// client that is refused again and again cannot use the whole trail up and make every real decision (and every stop) untraceable.
/// </summary>
public sealed class InMemoryAuditLog(int capacity = InMemoryAuditLog.DefaultCapacity) : IAuditLog
{
    public const int DefaultCapacity = 2000;

    /// <summary>The share of the trail that refusals may use, in quarters.</summary>
    private const int RefusalQuarters = 3;

    private readonly int _capacity = int.IsNegative(capacity) ? throw new ArgumentOutOfRangeException(nameof(capacity), "The capacity cannot be negative.") : capacity;
    private readonly int _refusalLimit = (int)(capacity * (long)RefusalQuarters / 4);
    private readonly object _lock = new();
    private readonly List<AuditEntry> _entries = [];

    public ValueTask<bool> TryRecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return TryRecordRangeAsync([entry], cancellationToken);
    }

    public ValueTask<bool> TryRecordRangeAsync(IReadOnlyList<AuditEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_lock)
        {
            var refusals = entries.Count(entry => AuditEvents.IsRefusal(entry.Event));
            var fits = _entries.Count + entries.Count <= _capacity && (refusals == 0 || _entries.Count + refusals <= _refusalLimit);
            if (fits)
            {
                _entries.AddRange(entries);
            }
            return ValueTask.FromResult(fits);
        }
    }

    public IReadOnlyList<AuditEntry> List()
    {
        lock (_lock)
        {
            return [.. Enumerable.Reverse(_entries)];
        }
    }
}
