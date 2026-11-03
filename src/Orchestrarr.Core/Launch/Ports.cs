namespace Orchestrarr.Core.Launch;

/// <summary>Signs an order. Held by the control plane only: the key is never readable (Vault transit in production).</summary>
public interface IOrderSigner
{
    ValueTask<string> SignAsync(byte[] payload, CancellationToken cancellationToken);
}

/// <summary>Verifies an order's signature. Held by the launcher only, which cannot sign.</summary>
public interface IOrderVerifier
{
    ValueTask<bool> VerifyAsync(byte[] payload, string signature, CancellationToken cancellationToken);
}

/// <summary>Remembers the nonces already used, so an order runs at most once.</summary>
public interface INonceLedger
{
    /// <summary>True the first time a nonce is seen, false after.</summary>
    ValueTask<bool> TryConsumeAsync(string nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}

/// <summary>The global stop (SPEC D-43).</summary>
public interface IKillSwitch
{
    bool Engaged { get; }
}

/// <summary>The audit trail. The product fails closed: an entry that cannot be recorded stops the action.</summary>
public interface IAuditSink
{
    /// <summary>False when the entry could not be recorded.</summary>
    ValueTask<bool> TryRecordAsync(AuditEntry entry, CancellationToken cancellationToken);
}

public sealed record AuditEntry(DateTimeOffset At, string Event, string Subject, string? OrderId, string? Detail);

/// <summary>The powers the operator granted per application and per environment (SPEC D-10, D-11).</summary>
public interface IGrantedPowers
{
    bool Allows(LaunchOrder order);
}
