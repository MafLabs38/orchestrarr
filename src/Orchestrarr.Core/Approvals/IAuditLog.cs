using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Approvals;

/// <summary>An entry of the trail with its number: the order in which it was recorded, counted from 1, which never changes.</summary>
public sealed record AuditRow(long Id, AuditEntry Entry);

/// <summary>
/// The audit trail with its read side, implemented in memory (the sandbox) and in a SQLite file (ADR 0013). The implementations are
/// synchronous: a call can wait for the file, no longer than its busy timeout.
/// </summary>
public interface IAuditLog : IAuditSink
{
    /// <summary>The newest entries with their numbers, newest first: at most <paramref name="limit"/>, since a trail in a file can hold hundreds of thousands.</summary>
    IReadOnlyList<AuditRow> Page(int limit);
}

public static class AuditLogExtensions
{
    /// <summary>The newest entries, newest first: at most <paramref name="limit"/>.</summary>
    public static IReadOnlyList<AuditEntry> List(this IAuditLog log, int limit = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(log);
        return [.. log.Page(limit).Select(row => row.Entry)];
    }
}
