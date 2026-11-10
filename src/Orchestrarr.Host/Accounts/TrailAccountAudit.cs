using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Host.Accounts;

/// <summary>
/// The accounts' events go to the audit trail of the product. The trail is bounded and fails closed for approvals; an account event that
/// does not fit is logged and does not stop the sign-in or the change it is about (the failures have the smaller share of the trail).
/// </summary>
public sealed class TrailAccountAudit(IAuditSink trail, TimeProvider time, ILogger<TrailAccountAudit> logger) : IAccountAudit
{
    public void Record(string actor, string action, string target, string? detail = null)
    {
        var entry = new AuditEntry(time.GetUtcNow(), action, actor, null, detail is null ? target : $"{target}: {detail}");
        // Both sinks finish synchronously (a call can wait for the file): nothing is left to await
        if (!trail.TryRecordAsync(entry, CancellationToken.None).AsTask().GetAwaiter().GetResult())
        {
            logger.LogWarning("The audit trail could not take the event {Action}", action);
        }
    }
}
