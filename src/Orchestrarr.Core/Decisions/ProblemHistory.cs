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
    string? Reason,
    HistoryOutcome? Outcome = null);

/// <summary>A problem that is set aside for this target.</summary>
public sealed record ActiveSkip(string RowId, string Target);

/// <summary>
/// What a read of the history found: the lines it could read, and where to continue from. The cursor is the last line looked at when the read was full, whether or not it
/// could be read (a line that cannot be read must not cut the following pages short); null when there is nothing more.
/// </summary>
public sealed record HistoryScan(IReadOnlyList<HistoryEntry> Lines, long? Cursor);

/// <summary>
/// The memory of what was decided (ADR 0027). Append-only apart from undoing a skip. It holds the photographs of the rows decided and, once it is known, the outcome of an
/// approval (the approvals and the runs are trimmed after a while; the history is not).
/// </summary>
public interface IProblemHistory
{
    /// <summary>Adds a line; the identifier of the argument is ignored and the new one is returned.</summary>
    long Record(HistoryEntry entry);

    /// <summary>Takes a skip back, so that the problem is shown again. False when there is no such skip, or it was taken back already.</summary>
    bool UndoSkip(long id, DateTimeOffset at);

    /// <summary>
    /// Writes down how an approved line ended, once: the approval and the run are bounded and trimmed, the history is not, so what became of a decision must outlive them.
    /// Does nothing for a line that already has its outcome.
    /// </summary>
    void Conclude(long id, HistoryOutcome outcome);

    /// <summary>The approved lines that have no outcome yet, oldest first, from the one after <paramref name="after"/>: what the concluding pass looks at.</summary>
    HistoryScan ReadUnconcluded(int limit, long after);

    /// <summary>
    /// Ends the skips of rows that are gone: a skip is for a problem, and when the problem has ended and comes back, it is a new one that must be seen. The lines stay in the
    /// history with the outcome <see cref="HistoryOutcome.Ended"/>. Returns how many skips were ended.
    /// </summary>
    int LapseSkips(IReadOnlyCollection<string> rowIds);

    /// <summary>The skips that are in force.</summary>
    IReadOnlyList<ActiveSkip> ActiveSkips();

    /// <summary>The lines, newest first, without the skips that were taken back. <paramref name="before"/> is the identifier to continue from.</summary>
    HistoryScan ReadPage(HistoryState? state, int limit, long? before);

}

/// <summary>The reads of the history without the cursor.</summary>
public static class ProblemHistoryExtensions
{
    public static IReadOnlyList<HistoryEntry> Page(this IProblemHistory history, HistoryState? state, int limit, long? before)
    {
        ArgumentNullException.ThrowIfNull(history);
        return history.ReadPage(state, limit, before).Lines;
    }

    public static IReadOnlyList<HistoryEntry> Unconcluded(this IProblemHistory history, int limit, long after = 0)
    {
        ArgumentNullException.ThrowIfNull(history);
        return history.ReadUnconcluded(limit, after).Lines;
    }
}

/// <summary>A history in memory, for the tests and for a sandbox that keeps nothing.</summary>
public sealed class InMemoryProblemHistory : IProblemHistory
{
    private readonly Lock _gate = new();
    private readonly List<HistoryEntry> _entries = [];
    private readonly HashSet<long> _undone = [];
    private readonly Dictionary<long, HistoryOutcome> _outcomes = [];
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

    public void Conclude(long id, HistoryOutcome outcome)
    {
        lock (_gate)
        {
            if (_entries.Any(entry => entry.Id == id && entry.State == HistoryState.Approved))
            {
                _outcomes.TryAdd(id, outcome);
            }
        }
    }

    public HistoryScan ReadUnconcluded(int limit, long after)
    {
        lock (_gate)
        {
            var lines = _entries.Where(entry => entry.State == HistoryState.Approved && entry.Id > after && !_outcomes.ContainsKey(entry.Id)).OrderBy(entry => entry.Id).Take(limit).ToList();
            return new HistoryScan(lines, lines.Count == limit ? lines[^1].Id : null);
        }
    }

    public int LapseSkips(IReadOnlyCollection<string> rowIds)
    {
        lock (_gate)
        {
            var lapsing = _entries.Where(entry => entry.State == HistoryState.Skipped && !_undone.Contains(entry.Id) && !_outcomes.ContainsKey(entry.Id) && rowIds.Contains(entry.RowId)).ToList();
            foreach (var entry in lapsing)
            {
                _outcomes[entry.Id] = HistoryOutcome.Ended;
            }
            return lapsing.Count;
        }
    }

    public bool UndoSkip(long id, DateTimeOffset at)
    {
        lock (_gate)
        {
            return _entries.Any(entry => entry.Id == id && entry.State == HistoryState.Skipped) && !_outcomes.ContainsKey(id) && _undone.Add(id);
        }
    }

    public IReadOnlyList<ActiveSkip> ActiveSkips()
    {
        lock (_gate)
        {
            return [.. _entries.Where(entry => entry.State == HistoryState.Skipped && !_undone.Contains(entry.Id) && !_outcomes.ContainsKey(entry.Id)).Select(entry => new ActiveSkip(entry.RowId, entry.Target))];
        }
    }

    public HistoryScan ReadPage(HistoryState? state, int limit, long? before)
    {
        lock (_gate)
        {
            var lines = _entries
                .Where(entry => (state is null || entry.State == state) && !_undone.Contains(entry.Id) && (before is null || entry.Id < before))
                .OrderByDescending(entry => entry.Id)
                .Take(limit)
                .Select(entry => _outcomes.TryGetValue(entry.Id, out var outcome) ? entry with { Outcome = outcome } : entry)
                .ToList();
            return new HistoryScan(lines, lines.Count == limit ? lines[^1].Id : null);
        }
    }
}
