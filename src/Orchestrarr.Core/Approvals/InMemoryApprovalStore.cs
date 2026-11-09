namespace Orchestrarr.Core.Approvals;

/// <summary>A bounded, in-memory store, the sandbox's (ADR 0010): a restart forgets it. The one in the file is the persistent one (ADR 0013).</summary>
public sealed class InMemoryApprovalStore(int capacity = InMemoryApprovalStore.DefaultCapacity) : IApprovalStore
{
    public const int DefaultCapacity = StoreLimits.MemoryQueue;

    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity), "The capacity must be greater than zero.");
    private readonly object _lock = new();
    private readonly List<Approval> _approvals = [];

    // Only the approvals that can still be carried out take room: an ended one is history, and it is trimmed when it grows past the capacity
    public int Free
    {
        get
        {
            lock (_lock)
            {
                return _capacity - _approvals.Count(approval => approval.IsOpen);
            }
        }
    }

    public bool Contains(Approval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        lock (_lock)
        {
            return _approvals.Exists(existing => existing.IsOpen && SameDecision(existing, approval));
        }
    }

    /// <summary>Adds all of them or none: false when one is already there or they do not all fit. Only the in-memory ledger writes here.</summary>
    public bool TryAddRange(IReadOnlyList<Approval> approvals)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        lock (_lock)
        {
            var distinct = approvals.Select(Key).Distinct().Count() == approvals.Count;
            var open = _approvals.Count(existing => existing.IsOpen);
            if (!distinct || approvals.Count > _capacity - open || approvals.Any(candidate => _approvals.Exists(existing => existing.IsOpen && SameDecision(existing, candidate))))
            {
                return false;
            }
            _approvals.AddRange(approvals);
            Trim();
            return true;
        }
    }

    public IReadOnlyList<Approval> List(int limit = int.MaxValue)
    {
        lock (_lock)
        {
            return [.. _approvals.OrderByDescending(approval => approval.ApprovedAt).Take(limit)];
        }
    }

    /// <summary>Moves the approvals of a batch to a new state. Only the in-memory queue calls this.</summary>
    internal void SetState(Guid batchId, ApprovalState state, DateTimeOffset at)
    {
        lock (_lock)
        {
            for (var i = 0; i < _approvals.Count; i++)
            {
                if (_approvals[i].BatchId == batchId)
                {
                    _approvals[i] = _approvals[i] with { State = state, EndedAt = state is ApprovalState.Waiting or ApprovalState.Taken ? null : at };
                }
            }
        }
    }

    // History is kept up to the capacity again: the oldest ended approvals go first, an open one never does
    private void Trim()
    {
        var excess = _approvals.Count - _capacity * 2;
        if (excess <= 0)
        {
            return;
        }
        foreach (var gone in _approvals.Where(approval => !approval.IsOpen).OrderBy(approval => approval.ApprovedAt).Take(excess).ToList())
        {
            _approvals.Remove(gone);
        }
    }

    private static bool SameDecision(Approval left, Approval right) => Key(left) == Key(right);

    private static string Key(Approval approval) =>
        $"{approval.Repository}#{approval.PullRequest}@{approval.HeadSha}:{string.Join(',', approval.Steps.Order())}";
}
