namespace Orchestrarr.Core.Launch;

public sealed class LauncherOptions
{
    /// <summary>The longest life an order may claim, whatever it says: a bound the issuer cannot widen.</summary>
    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Tolerated difference between the clocks of the issuer and the launcher.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The only component that can start an agent run, and it only does so from a valid order (SPEC D-41, D-46). It verifies, it cannot
/// sign. This class decides whether to start; starting the run itself comes with the vertical that acts.
/// </summary>
public sealed class Launcher(
    IOrderVerifier verifier,
    INonceLedger nonces,
    IGrantedPowers powers,
    IKillSwitch killSwitch,
    IAuditSink audit,
    TimeProvider time,
    LauncherOptions options)
{
    public async ValueTask<LaunchDecision> CheckAsync(SignedOrder signed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signed);
        var order = signed.Order;
        var now = time.GetUtcNow();

        var refusal = await FirstRefusalAsync(signed, now, cancellationToken);
        var entry = new AuditEntry(now, refusal is null ? "launch.accepted" : "launch.refused", order.Approver, order.Id.ToString("D"), refusal?.ToString());
        if (!await audit.TryRecordAsync(entry, cancellationToken))
        {
            return new LaunchDecision(LaunchRefusal.AuditUnavailable);
        }
        return new LaunchDecision(refusal);
    }

    private async ValueTask<LaunchRefusal?> FirstRefusalAsync(SignedOrder signed, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var order = signed.Order;
        var settings = options;

        if (killSwitch.Engaged)
        {
            return LaunchRefusal.KillSwitchEngaged;
        }
        if (!await verifier.VerifyAsync(order.Canonical(), signed.Signature, cancellationToken))
        {
            return LaunchRefusal.BadSignature;
        }
        if (order.IssuedAt - settings.ClockSkew > now)
        {
            return LaunchRefusal.NotYetValid;
        }
        if (order.ExpiresAt <= now)
        {
            return LaunchRefusal.Expired;
        }
        if (order.ExpiresAt - order.IssuedAt > settings.MaxLifetime)
        {
            return LaunchRefusal.LifetimeTooLong;
        }
        if (order.Steps.Count == 0 || !powers.Allows(order))
        {
            return LaunchRefusal.OutsideGrantedPowers;
        }
        // Last, so that an order refused for another reason does not burn its nonce.
        return await nonces.TryConsumeAsync(order.Nonce, order.ExpiresAt, cancellationToken) ? null : LaunchRefusal.Replayed;
    }
}
