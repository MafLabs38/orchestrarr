using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Approvals;

/// <summary>The audit trail with its read side, implemented in memory (the sandbox) and in a SQLite file (ADR 0013).</summary>
public interface IAuditLog : IAuditSink
{
    /// <summary>The entries, newest first.</summary>
    IReadOnlyList<AuditEntry> List();
}
