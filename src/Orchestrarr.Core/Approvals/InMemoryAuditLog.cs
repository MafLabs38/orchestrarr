using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Approvals;

/// <summary>
/// The audit trail in memory and bounded: the sandbox's store, not durable; the persistent, append-only trail is <c>SqliteAuditLog</c> (ADR 0013).
/// Past its capacity it refuses, so that nothing happens without a trace, and refusals have only a share of it (<see cref="AuditCapacityPolicy"/>).
/// </summary>
public sealed class InMemoryAuditLog(int capacity = InMemoryAuditLog.DefaultCapacity) : IAuditLog
{
    public const int DefaultCapacity = StoreLimits.MemoryAudit;

    private readonly int _capacity = int.IsNegative(capacity) ? throw new ArgumentOutOfRangeException(nameof(capacity), "The capacity cannot be negative.") : capacity;
    private readonly object _lock = new();
    private readonly List<AuditEntry> _entries = [];
    private int _refusals;

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
            var fits = AuditCapacityPolicy.Fits(_capacity, _entries.Count, _refusals, entries.Count, refusals);
            if (fits)
            {
                _entries.AddRange(entries);
                _refusals += refusals;
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
