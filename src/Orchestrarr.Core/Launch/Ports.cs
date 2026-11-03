namespace Orchestrarr.Core.Launch;

// Every port is asynchronous because every real implementation does I/O, and every one fails closed: when it cannot answer, the answer
// is the refusing one (killed, denied, production), never the permissive one.

/// <summary>Signs an order. Held by the control plane only: the key is never readable (Vault transit in production).</summary>
public interface IOrderSigner
{
    ValueTask<string> SignAsync(byte[] payload, CancellationToken cancellationToken);
}

/// <summary>
/// Verifies an order's signature. Held by the launcher only, which cannot sign: the scheme must be **asymmetric** (the verifier holds a
/// public key), because with a shared secret the launcher could forge an order. The implementation compares in constant time and binds
/// the key version (Vault transit's <c>vault:vN:</c> prefix).
/// </summary>
public interface IOrderVerifier
{
    ValueTask<bool> VerifyAsync(byte[] payload, string signature, CancellationToken cancellationToken);
}

/// <summary>Remembers the orders already accepted, so an order runs at most once.</summary>
public interface INonceLedger
{
    /// <summary>
    /// True the first time an order id is seen, false after. The entry must be kept until <paramref name="retainUntil"/>, which already
    /// includes the clock tolerance: a ledger that forgets sooner makes a still-valid order replayable.
    /// </summary>
    ValueTask<bool> TryConsumeAsync(string orderId, DateTimeOffset retainUntil, CancellationToken cancellationToken);
}

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

/// <summary>The powers the operator granted per application and per environment (SPEC D-10, D-11). Missing means denied.</summary>
public interface IGrantedPowers
{
    ValueTask<bool> AllowsAsync(LaunchOrder order, CancellationToken cancellationToken);
}

/// <summary>
/// Says whether an environment is production, from the environment description (SPEC D-17, ADR 0002), never from the request. An
/// environment the description does not know is production.
/// </summary>
public interface IEnvironmentClassifier
{
    ValueTask<bool> IsProductionAsync(string environment, CancellationToken cancellationToken);
}

public static class AuditEvents
{
    public const string Signing = "order.signing";
    public const string SigningFailed = "order.signing-failed";
    public const string Issued = "order.issued";
    public const string IssueRefused = "order.refused";
    public const string LaunchAccepted = "launch.accepted";
    public const string LaunchRefused = "launch.refused";
    public const string ApprovalRecorded = "approval.recorded";
    public const string ApprovalRefused = "approval.refused";
    public const string KillSwitchEngaged = "killswitch.engaged";
    public const string KillSwitchReleased = "killswitch.released";

    /// <summary>A database that did not exist was created: whatever history there was is gone, so the stop starts engaged until someone releases it.</summary>
    public const string InstanceCreated = "instance.created";

    /// <summary>Who the trail names when the order or the approver could not be verified: what an unverified party claims is never written.</summary>
    public const string Unverified = "unverified";

    /// <summary>Who the trail names for what the product itself does, with nobody behind it.</summary>
    public const string System = "system";

    /// <summary>A refusal, as opposed to a decision or a stop: refusals can be provoked by anyone, so they have the smaller share of a bounded trail.</summary>
    public static bool IsRefusal(string name) => name is IssueRefused or LaunchRefused or ApprovalRefused;

    /// <summary>A trace of the stop, or of a new database: the last part of a bounded trail is kept for them, so that decisions cannot leave the stop untraceable.</summary>
    public static bool UsesReserve(string name) => name is KillSwitchEngaged or KillSwitchReleased or InstanceCreated;
}
