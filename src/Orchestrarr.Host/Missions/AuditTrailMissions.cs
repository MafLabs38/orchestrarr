using System.Security.Cryptography;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Agents;
using Orchestrarr.Host.Storage;
using Orchestrarr.Storage;

namespace Orchestrarr.Host.Missions;

/// <summary>Writes the events of the missions to the audit trail, in order, with the others (ADR 0013).</summary>
internal sealed class AuditTrailMissions(IAuditSink sink, TimeProvider time) : IMissionAudit
{
    public ValueTask<bool> RecordAsync(string eventName, string subject, string? detail, CancellationToken cancellationToken) =>
        sink.TryRecordAsync(new AuditEntry(time.GetUtcNow(), eventName, subject.Length > 120 ? subject[..120] : subject, null, detail is { Length: > 400 } ? detail[..400] : detail), cancellationToken);
}
