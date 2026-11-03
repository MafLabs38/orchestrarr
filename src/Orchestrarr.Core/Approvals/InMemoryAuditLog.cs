using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Approvals;

/// <summary>The audit trail with its read side, so that a persistent trail replaces the in-memory one without touching the routes.</summary>
public interface IAuditLog : IAuditSink
{
    /// <summary>The entries, newest first.</summary>
    IReadOnlyList<AuditEntry> List();
}

/// <summary>
/// The audit trail in memory and bounded: the sandbox's store, not durable. Past its capacity it refuses, so that nothing happens without a
/// trace; the persistent trail that SPEC D-43 and F-SAF-02 require comes with the workshop. Refusals have a smaller budget than the
/// rest: a client that is refused again and again cannot use the whole trail up and make every real decision (and every stop) untraceable.
/// </summary>
public sealed class InMemoryAuditLog(int capacity = 2000) : IAuditLog
{
    private readonly int _refusalBudget = capacity * 3 / 4;

    private readonly object _lock = new();
    private readonly List<AuditEntry> _entries = [];

    public ValueTask<bool> TryRecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            var limit = entry.Event.EndsWith(".refused", StringComparison.Ordinal) ? _refusalBudget : capacity;
            if (_entries.Count >= limit)
            {
                return ValueTask.FromResult(false);
            }
            _entries.Add(entry);
            return ValueTask.FromResult(true);
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
