using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Approvals;

/// <summary>The audit trail with its read side, implemented in memory (the sandbox) and in a SQLite file (ADR 0013).</summary>
public interface IAuditLog : IAuditSink
{
    /// <summary>The newest entries, newest first: at most <paramref name="limit"/>, since a trail in a file can hold hundreds of thousands.</summary>
    IReadOnlyList<AuditEntry> List(int limit = int.MaxValue);
}
