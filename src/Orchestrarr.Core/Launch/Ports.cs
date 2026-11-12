namespace Orchestrarr.Core.Launch;

// Every port is asynchronous because every real implementation does I/O, and every one fails closed: when it cannot answer, the answer
// is the refusing one (killed, denied), never the permissive one.

/// <summary>The global stop (SPEC D-43). Unknown means engaged.</summary>
public interface IKillSwitch
{
    ValueTask<bool> IsEngagedAsync(CancellationToken cancellationToken);
}

/// <summary>The audit trail. The product fails closed: an entry that cannot be recorded stops the action.</summary>
public interface IAuditSink
{
    /// <summary>False when the entry could not be recorded.</summary>
    ValueTask<bool> TryRecordAsync(AuditEntry entry, CancellationToken cancellationToken);

    /// <summary>All the entries or none: false, and nothing written, when they cannot all be recorded.</summary>
    ValueTask<bool> TryRecordRangeAsync(IReadOnlyList<AuditEntry> entries, CancellationToken cancellationToken);
}

public sealed record AuditEntry(DateTimeOffset At, string Event, string Subject, string? OrderId, string? Detail);

public static class AuditEvents
{
    public const string ApprovalRecorded = "approval.recorded";
    public const string ApprovalRefused = "approval.refused";
    public const string BatchQueued = "batch.queued";
    public const string BatchDelivered = "batch.delivered";
    public const string BatchDone = "batch.done";
    public const string BatchRefused = "batch.refused";
    public const string BatchExpired = "batch.expired";
    public const string ProblemSkipped = "problem.skipped";
    public const string ProblemUnskipped = "problem.unskipped";
    public const string KillSwitchEngaged = "killswitch.engaged";
    public const string KillSwitchReleased = "killswitch.released";

    /// <summary>The entry that says a store was created, so the global stop starts engaged until someone releases it: one text for every kind of store.</summary>
    public static AuditEntry InstanceCreatedEntry(DateTimeOffset at, string store) =>
        new(at, InstanceCreated, System, null, $"the {store} was created: the global stop starts engaged");

    /// <summary>A database that did not exist was created: whatever history there was is gone, so the stop starts engaged until someone releases it.</summary>
    public const string InstanceCreated = "instance.created";

    /// <summary>Who the trail names when the order or the approver could not be verified: what an unverified party claims is never written.</summary>
    public const string Unverified = "unverified";

    /// <summary>Who the trail names for what the product itself does, with nobody behind it.</summary>
    public const string System = "system";

    /// <summary>A refusal, as opposed to a decision or a stop: refusals can be provoked by anyone, so they have the smaller share of a bounded trail.</summary>
    public static bool IsRefusal(string name) => name is ApprovalRefused || Accounts.AccountEvents.IsFailure(name);

    /// <summary>A trace of the stop, or of a new database: the last part of a bounded trail is kept for them, so that decisions cannot leave the stop untraceable.</summary>
    public static bool UsesReserve(string name) => name is KillSwitchEngaged or KillSwitchReleased or InstanceCreated;
}
