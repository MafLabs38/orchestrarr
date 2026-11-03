namespace Orchestrarr.Core.Launch;

public sealed record LauncherOptions
{
    /// <summary>The longest life an order may claim, whatever it says: a bound the issuer cannot widen.</summary>
    public TimeSpan MaxLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Tolerated difference between the clocks of the issuer and the launcher, applied to the issue time only.</summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How old a second factor may be, when the order was issued, for production.</summary>
    public TimeSpan StepUpMaxAge { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The launcher's own name: an order for another is refused.</summary>
    public string Audience { get; init; } = "launcher";
}

/// <summary>What the launcher asks to know about an order beyond its signature: whether the powers are granted, and what an environment is.</summary>
public sealed record LaunchChecks(IGrantedPowers Powers, IEnvironmentClassifier Environments);

/// <summary>
/// The only component that can start an agent run, and it only does so from a valid order (SPEC D-41, D-46). It verifies, it cannot
/// sign (it has no signer). It answers with the verified, normalized order: that is what is to be run, not the object it was handed.
/// This class decides whether to start; starting the run itself, the pull from the store, the concurrency and the budget come with the
/// vertical that acts.
/// </summary>
public sealed class Launcher(
    IOrderVerifier verifier,
    INonceLedger nonces,
    LaunchChecks checks,
    IKillSwitch killSwitch,
    IAuditSink audit,
    TimeProvider time,
    LauncherOptions options)
{
    public async ValueTask<LaunchDecision> CheckAsync(SignedOrder signed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signed);
        var now = time.GetUtcNow();
        var order = signed.Order.Normalized();

        var (refusal, trusted) = await FirstRefusalAsync(signed, order, now, cancellationToken);
        if (!await audit.TryRecordAsync(Entry(now, order, refusal, trusted), cancellationToken))
        {
            return new LaunchDecision(LaunchRefusal.AuditUnavailable, null);
        }
        return new LaunchDecision(refusal, refusal is null ? order : null);
    }

    // Until the signature is verified and the fields are valid, nothing of the order is trusted, not even to be written to the trail.
    private static AuditEntry Entry(DateTimeOffset now, LaunchOrder order, LaunchRefusal? refusal, bool trusted)
    {
        var name = refusal is null ? AuditEvents.LaunchAccepted : AuditEvents.LaunchRefused;
        return trusted
            ? new AuditEntry(now, name, order.ApproverSubject, order.Id.ToString("D"), $"{refusal?.ToString() ?? "accepted"} {OrderIssuer.Describe(order)}")
            : new AuditEntry(now, name, AuditEvents.Unverified, null, refusal?.ToString());
    }

    private async ValueTask<(LaunchRefusal? Refusal, bool Trusted)> FirstRefusalAsync(
        SignedOrder signed, LaunchOrder order, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await killSwitch.IsEngagedAsync(cancellationToken))
        {
            return (LaunchRefusal.KillSwitchEngaged, false);
        }

        bool signatureValid;
        try
        {
            signatureValid = await verifier.VerifyAsync(order.Canonical(), signed.Signature, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A verifier that cannot answer (a sealed Vault, a malformed signature it chokes on) never lets an order through.
            return (LaunchRefusal.VerifierUnavailable, false);
        }
        if (!signatureValid)
        {
            return (LaunchRefusal.BadSignature, false);
        }
        if (!OrderValidation.IsValid(order.WorkItemId, order.Repository, order.HeadSha, order.Environment, order.Steps, order.ApproverSubject, order.Audience))
        {
            return (LaunchRefusal.Malformed, false);
        }

        var refusal = await CheckTrustedAsync(order, now, cancellationToken);
        return (refusal, true);
    }

    private async ValueTask<LaunchRefusal?> CheckTrustedAsync(LaunchOrder order, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (order.Audience != options.Audience)
        {
            return LaunchRefusal.WrongAudience;
        }
        if (order.IssuedAt - options.ClockSkew > now)
        {
            return LaunchRefusal.NotYetValid;
        }
        if (order.ExpiresAt <= now)
        {
            return LaunchRefusal.Expired;
        }
        if (order.ExpiresAt - order.IssuedAt > options.MaxLifetime)
        {
            return LaunchRefusal.LifetimeTooLong;
        }
        // The launcher works out production itself and checks the second factor the order carries: the approval is authorized twice.
        if (order.Environment is not null
            && await checks.Environments.IsProductionAsync(order.Environment, cancellationToken)
            && !(order.StepUpAt is { } at && at <= order.IssuedAt && order.IssuedAt - at <= options.StepUpMaxAge))
        {
            return LaunchRefusal.StepUpRequired;
        }
        if (!await checks.Powers.AllowsAsync(order, cancellationToken))
        {
            return LaunchRefusal.OutsideGrantedPowers;
        }
        // Last, so that an order refused for another reason does not burn its id. Kept until the order could no longer pass the clock check.
        if (!await nonces.TryConsumeAsync(order.Id.ToString("D"), order.ExpiresAt + options.ClockSkew, cancellationToken))
        {
            return LaunchRefusal.Replayed;
        }
        // The switch may have been engaged while the checks ran: read it again before accepting. The id stays spent, which fails closed.
        return await killSwitch.IsEngagedAsync(cancellationToken) ? LaunchRefusal.KillSwitchEngaged : null;
    }
}
