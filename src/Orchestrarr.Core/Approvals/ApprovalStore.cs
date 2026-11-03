using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Approvals;

/// <summary>Where the approvals wait for a launcher. The first implementation is in memory; a persistent one comes with the workshop.</summary>
public interface IApprovalStore
{
    /// <summary>False when the same decision (same pull request, commit and steps) is already waiting, or the store is full.</summary>
    ApprovalStoreResult TryAdd(Approval approval);

    /// <summary>The approvals, newest first.</summary>
    IReadOnlyList<Approval> List();
}

public enum ApprovalStoreResult
{
    Added,
    Duplicate,
    Full,
}

/// <summary>A bounded, in-memory store. It never drops an approval to make room: when full, it refuses.</summary>
public sealed class InMemoryApprovalStore(int capacity = 500) : IApprovalStore
{
    private readonly object _lock = new();
    private readonly List<Approval> _approvals = [];

    public ApprovalStoreResult TryAdd(Approval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        lock (_lock)
        {
            if (_approvals.Any(existing => SameDecision(existing, approval)))
            {
                return ApprovalStoreResult.Duplicate;
            }
            if (_approvals.Count >= capacity)
            {
                return ApprovalStoreResult.Full;
            }
            _approvals.Add(approval);
            return ApprovalStoreResult.Added;
        }
    }

    public IReadOnlyList<Approval> List()
    {
        lock (_lock)
        {
            return [.. _approvals.OrderByDescending(approval => approval.ApprovedAt)];
        }
    }

    private static bool SameDecision(Approval left, Approval right) =>
        left.Repository == right.Repository
        && left.PullRequest == right.PullRequest
        && left.HeadSha == right.HeadSha
        && left.Steps.Order().SequenceEqual(right.Steps.Order());
}

/// <summary>The global stop, in memory (SPEC D-43). Engaged, nothing is approved and nothing launches.</summary>
public sealed class InMemoryKillSwitch : IKillSwitch
{
    private volatile bool _engaged;

    public bool Engaged => _engaged;

    public void Set(bool engaged) => _engaged = engaged;

    public ValueTask<bool> IsEngagedAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_engaged);
}

/// <summary>
/// The audit trail, in memory and bounded: the sandbox's store. It is not durable, which the interface says, and the persistent trail that
/// D-43 requires comes with the workshop. It does not drop entries silently: past its capacity it refuses, so that nothing happens
/// without a trace.
/// </summary>
public sealed class InMemoryAuditLog(int capacity = 2000) : IAuditSink
{
    private readonly object _lock = new();
    private readonly List<AuditEntry> _entries = [];

    public ValueTask<bool> TryRecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            if (_entries.Count >= capacity)
            {
                return ValueTask.FromResult(false);
            }
            _entries.Add(entry);
            return ValueTask.FromResult(true);
        }
    }

    /// <summary>The entries, newest first.</summary>
    public IReadOnlyList<AuditEntry> List()
    {
        lock (_lock)
        {
            return [.. Enumerable.Reverse(_entries)];
        }
    }
}
