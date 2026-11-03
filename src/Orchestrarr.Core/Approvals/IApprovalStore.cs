namespace Orchestrarr.Core.Approvals;

/// <summary>
/// Where the approvals wait for a launcher. The store owns the rule of what is the same decision (same repository, pull request, commit and
/// steps), so that no caller keeps its own copy of it. The service is its only writer; a persistent store must also enforce the uniqueness
/// itself (a unique key), because one process is not guaranteed.
/// </summary>
public interface IApprovalStore
{
    /// <summary>How many more approvals fit. A store that is full refuses, it never drops one to make room.</summary>
    int Free { get; }

    /// <summary>Whether this decision is already waiting.</summary>
    bool Contains(Approval approval);

    /// <summary>Adds all of them or none: false when one is already there or they do not all fit.</summary>
    bool TryAddRange(IReadOnlyList<Approval> approvals);

    /// <summary>The approvals, newest first.</summary>
    IReadOnlyList<Approval> List();
}
