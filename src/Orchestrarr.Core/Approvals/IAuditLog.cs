using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Approvals;

/// <summary>The audit trail with its read side, so that a persistent trail replaces the in-memory one without touching the routes.</summary>
public interface IAuditLog : IAuditSink
{
    /// <summary>The entries, newest first.</summary>
    IReadOnlyList<AuditEntry> List();
}
