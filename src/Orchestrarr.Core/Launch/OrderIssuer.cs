namespace Orchestrarr.Core.Launch;

public sealed record OrderOptions
{
    /// <summary>How long an order can be used after it was issued. It covers the hand-over to the launcher, not a wait in a queue.</summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How old a second factor may be for an approval that touches production.</summary>
    public TimeSpan StepUpMaxAge { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The launcher the order is for: the launcher refuses any other.</summary>
    public string Audience { get; init; } = "launcher";
}

/// <summary>
/// The control plane's side: authorizes whoever gives the order, then builds and signs it. It cannot verify or execute one, and it
/// holds no credential that acts on an environment (ADR 0007). Nothing is signed without a trace, and a refusal never carries an order.
/// </summary>
public sealed class OrderIssuer(
    IOrderSigner signer,
    IKillSwitch killSwitch,
    IAuditSink audit,
    IEnvironmentClassifier environments,
    TimeProvider time,
    OrderOptions options)
{
    public async ValueTask<IssueResult> IssueAsync(Approver approver, OrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(request);

        var now = LaunchOrder.Seconds(time.GetUtcNow());
        var refusal = await CheckAsync(approver, request, now, cancellationToken);
        if (refusal is not null)
        {
            return await RefuseAsync(approver, refusal.Value, now, cancellationToken);
        }

        var order = new LaunchOrder(
            Guid.NewGuid(),
            request.WorkItemId,
            request.Repository,
            request.HeadSha,
            request.Environment,
            [.. request.Steps],
            approver.Subject,
            approver.StepUpAt,
            options.Audience,
            now,
            now + options.Lifetime).Normalized();

        // Recorded before it is signed: no trace, no order.
        if (!await audit.TryRecordAsync(Entry(now, AuditEvents.Signing, order), cancellationToken))
        {
            return new IssueResult(null, IssueRefusal.AuditUnavailable);
        }

        string signature;
        try
        {
            signature = await signer.SignAsync(order.Canonical(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A sealed or unreachable signer is a refusal, and the trail says the signing was attempted and failed.
            await audit.TryRecordAsync(Entry(now, AuditEvents.SigningFailed, order), cancellationToken);
            return new IssueResult(null, IssueRefusal.SignerUnavailable);
        }

        // The switch may have been engaged while the audit and the signer answered: read again before handing the order out.
        if (await killSwitch.IsEngagedAsync(cancellationToken))
        {
            return new IssueResult(null, IssueRefusal.KillSwitchEngaged);
        }
        if (!await audit.TryRecordAsync(Entry(now, AuditEvents.Issued, order), cancellationToken))
        {
            return new IssueResult(null, IssueRefusal.AuditUnavailable);
        }
        return new IssueResult(new SignedOrder(order, signature), null);
    }

    private async ValueTask<IssueRefusal?> CheckAsync(Approver approver, OrderRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await killSwitch.IsEngagedAsync(cancellationToken))
        {
            return IssueRefusal.KillSwitchEngaged;
        }
        if (!approver.IsPerson)
        {
            return IssueRefusal.NotAPerson;
        }
        if (!approver.IsOperator)
        {
            return IssueRefusal.NotAnOperator;
        }
        if (!OrderValidation.IsValid(request.WorkItemId, request.Repository, request.HeadSha, request.Environment, request.Steps, approver.Subject, options.Audience))
        {
            return IssueRefusal.InvalidRequest;
        }
        // Production is decided here from the environment description, never from what the caller says.
        if (request.Environment is not null
            && await environments.IsProductionAsync(request.Environment, cancellationToken)
            && !HasFreshStepUp(approver, now))
        {
            return IssueRefusal.StepUpRequired;
        }
        return null;
    }

    private bool HasFreshStepUp(Approver approver, DateTimeOffset now) =>
        approver.StepUpAt is { } at && at <= now && now - at <= options.StepUpMaxAge;

    private async ValueTask<IssueResult> RefuseAsync(Approver approver, IssueRefusal refusal, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // A refusal is worth a trace, but it does not depend on one: the refusal already fails closed. The subject is the identity
        // provider's, but the request is not recorded: its fields have not been validated.
        var subject = OrderValidation.IsValidSubject(approver.Subject) ? approver.Subject : "unverified";
        await audit.TryRecordAsync(new AuditEntry(now, AuditEvents.IssueRefused, subject, null, refusal.ToString()), cancellationToken);
        return new IssueResult(null, refusal);
    }

    internal static AuditEntry Entry(DateTimeOffset at, string name, LaunchOrder order) =>
        new(at, name, order.ApproverSubject, order.Id.ToString("D"), Describe(order));

    /// <summary>What was approved, from validated fields only: the commit, the environment, the steps, whether a step-up backs it.</summary>
    internal static string Describe(LaunchOrder order) =>
        $"{order.Fingerprint()} {order.Repository}@{order.HeadSha} env={order.Environment ?? "-"} steps={string.Join(',', order.Steps)} stepUp={order.StepUpAt is not null}";
}
