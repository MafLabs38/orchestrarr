namespace Orchestrarr.Core.Approvals;

/// <summary>A bounded, in-memory store (ADR 0010). Restarting forgets it, which a sandbox accepts and production does not.</summary>
public sealed class InMemoryApprovalStore(int capacity = InMemoryApprovalStore.DefaultCapacity) : IApprovalStore
{
    public const int DefaultCapacity = 500;

    private readonly int _capacity = int.IsPositive(capacity) ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity), "The capacity must be greater than zero.");
    private readonly object _lock = new();
    private readonly List<Approval> _approvals = [];

    public int Free
    {
        get
        {
            lock (_lock)
            {
                return _capacity - _approvals.Count;
            }
        }
    }

    public bool Contains(Approval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        lock (_lock)
        {
            return _approvals.Exists(existing => SameDecision(existing, approval));
        }
    }

    public bool TryAddRange(IReadOnlyList<Approval> approvals)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        lock (_lock)
        {
            var distinct = approvals.Select(Key).Distinct().Count() == approvals.Count;
            if (!distinct || approvals.Count > _capacity - _approvals.Count || approvals.Any(candidate => _approvals.Exists(existing => SameDecision(existing, candidate))))
            {
                return false;
            }
            _approvals.AddRange(approvals);
            return true;
        }
    }

    public IReadOnlyList<Approval> List()
    {
        lock (_lock)
        {
            return [.. _approvals.OrderByDescending(approval => approval.ApprovedAt)];
        }
    }

    private static bool SameDecision(Approval left, Approval right) => Key(left) == Key(right);

    private static string Key(Approval approval) =>
        $"{approval.Repository}#{approval.PullRequest}@{approval.HeadSha}:{string.Join(',', approval.Steps.Order())}";
}
