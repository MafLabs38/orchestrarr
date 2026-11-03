namespace Orchestrarr.Core.Approvals;

/// <summary>
/// Where the approvals wait for a launcher, as they are read. Writing goes through <see cref="IApprovalLedger"/> only, which records an approval
/// together with its trace: no caller can add an approval that leaves no trace. A persistent store enforces the uniqueness of a decision
/// itself (a unique key), because one process is not guaranteed.
/// </summary>
public interface IApprovalStore
{
    /// <summary>How many more approvals fit. A store that is full refuses, it never drops one to make room.</summary>
    int Free { get; }

    /// <summary>Whether this decision is already waiting.</summary>
    bool Contains(Approval approval);

    /// <summary>The approvals, newest first.</summary>
    IReadOnlyList<Approval> List(int limit = int.MaxValue);
}
