namespace Orchestrarr.Core.Decisions;

/// <summary>What the history keeps a line for: a problem that was approved (it went to the Runner), or one that a person set aside.</summary>
public enum HistoryState
{
    Approved,
    Skipped,
}

/// <summary>
/// One line of the history: a photograph of the row at the moment of the decision, because the live row is gone once the pull request is merged or the image is
/// updated. <c>Target</c> is what the decision was about (the description of the row: «express 4.16.0 → 5.3.0»): a skipped problem comes back when its target changes.
/// </summary>
public sealed record HistoryEntry(
    long Id,
    string RowId,
    string Target,
    HistoryState State,
    string Application,
    ProblemKind Kind,
    string Description,
    string? Repository,
    int? PullRequest,
    string? HeadSha,
    Guid? ApprovalId,
    Guid? BatchId,
    DateTimeOffset At,
    string By,
    string? Reason);

/// <summary>A problem that is set aside for this target.</summary>
public sealed record ActiveSkip(string RowId, string Target);

/// <summary>
/// The memory of what was decided (ADR 0027). Append-only apart from undoing a skip. It holds the photographs and nothing else: the outcome of an approval is read from
/// the approvals and the runs, where it already lives.
/// </summary>
public interface IProblemHistory
{
    /// <summary>Adds a line; the identifier of the argument is ignored and the new one is returned.</summary>
    long Record(HistoryEntry entry);

    /// <summary>Takes a skip back, so that the problem is shown again. False when there is no such skip, or it was taken back already.</summary>
    bool UndoSkip(long id, DateTimeOffset at);

    /// <summary>The skips that are in force.</summary>
    IReadOnlyList<ActiveSkip> ActiveSkips();

    /// <summary>The lines, newest first, without the skips that were taken back. <paramref name="before"/> is the identifier to continue from.</summary>
    IReadOnlyList<HistoryEntry> Page(HistoryState? state, int limit, long? before);
}

/// <summary>A history in memory, for the tests and for a sandbox that keeps nothing.</summary>
public sealed class InMemoryProblemHistory : IProblemHistory
{
    private readonly Lock _gate = new();
    private readonly List<HistoryEntry> _entries = [];
    private readonly HashSet<long> _undone = [];
    private long _last;

    public long Record(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            var id = ++_last;
            _entries.Add(entry with { Id = id });
            return id;
        }
    }

    public bool UndoSkip(long id, DateTimeOffset at)
    {
        lock (_gate)
        {
            return _entries.Any(entry => entry.Id == id && entry.State == HistoryState.Skipped) && _undone.Add(id);
        }
    }

    public IReadOnlyList<ActiveSkip> ActiveSkips()
    {
        lock (_gate)
        {
            return [.. _entries.Where(entry => entry.State == HistoryState.Skipped && !_undone.Contains(entry.Id)).Select(entry => new ActiveSkip(entry.RowId, entry.Target))];
        }
    }

    public IReadOnlyList<HistoryEntry> Page(HistoryState? state, int limit, long? before)
    {
        lock (_gate)
        {
            return [.. _entries
                .Where(entry => (state is null || entry.State == state) && !_undone.Contains(entry.Id) && (before is null || entry.Id < before))
                .OrderByDescending(entry => entry.Id)
                .Take(limit)];
        }
    }
}
