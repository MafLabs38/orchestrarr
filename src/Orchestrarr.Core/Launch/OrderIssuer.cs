using System.Security.Cryptography;

namespace Orchestrarr.Core.Launch;

public sealed class OrderOptions
{
    /// <summary>How long an order can be used after it was issued.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// The control plane's side: authorizes whoever gives the order, then builds and signs it. It cannot verify or execute one, and it
/// holds no credential that acts on an environment (ADR 0007).
/// </summary>
public sealed class OrderIssuer(
    IOrderSigner signer,
    IKillSwitch killSwitch,
    IAuditSink audit,
    TimeProvider time,
    OrderOptions options)
{
    public async ValueTask<IssueResult> IssueAsync(Approver approver, OrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(request);

        var refusal = Check(approver, request);
        if (refusal is not null)
        {
            return await RefuseAsync(approver, request, refusal.Value, cancellationToken);
        }

        var now = time.GetUtcNow();
        var order = new LaunchOrder(
            Guid.NewGuid(),
            request.WorkItemId,
            request.Repository,
            request.HeadSha,
            request.Environment,
            request.Steps,
            approver.Subject,
            now,
            now + options.Lifetime,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));

        // Recorded before it is signed: no trace, no order.
        if (!await audit.TryRecordAsync(new AuditEntry(now, "order.issued", approver.Subject, order.Id.ToString("D"), request.Repository), cancellationToken))
        {
            return new IssueResult(null, IssueRefusal.AuditUnavailable);
        }

        var signature = await signer.SignAsync(order.Canonical(), cancellationToken);
        return new IssueResult(new SignedOrder(order, signature), null);
    }

    private IssueRefusal? Check(Approver approver, OrderRequest request)
    {
        if (killSwitch.Engaged)
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
        if (request.TouchesProduction && !approver.HasStepUp)
        {
            return IssueRefusal.StepUpRequired;
        }
        return request.Steps.Count == 0 ? IssueRefusal.NoStepsTicked : null;
    }

    private async ValueTask<IssueResult> RefuseAsync(Approver approver, OrderRequest request, IssueRefusal refusal, CancellationToken cancellationToken)
    {
        // A refusal is worth a trace, but it does not depend on one: the refusal already fails closed.
        await audit.TryRecordAsync(new AuditEntry(time.GetUtcNow(), "order.refused", approver.Subject, null, $"{refusal}:{request.Repository}"), cancellationToken);
        return new IssueResult(null, refusal);
    }
}
