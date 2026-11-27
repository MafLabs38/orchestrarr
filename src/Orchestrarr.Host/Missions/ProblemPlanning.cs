using System.Collections.Concurrent;

namespace Orchestrarr.Host.Missions;

/// <summary>A validated row that the lead is planning: what it is, on which lot, since when (ADR 0056).</summary>
public sealed record PlannedRow(string ProblemId, string Title, string? Kind, string Repository, DateTimeOffset Since);

/// <summary>
/// The validated rows whose plan the lead is making (ADR 0056): between the validation and the line, a decision is taken and not placed yet. The table of decisions does not offer them again, and the Flow screen shows them at the entry of
/// the line, as the lead plans them. In memory, for the time of a validation.
/// </summary>
public sealed class ProblemPlanning
{
    private readonly ConcurrentDictionary<string, PlannedRow> _planning = new(StringComparer.Ordinal);

    public void Add(PlannedRow row) => _planning[row.ProblemId] = row;

    public void Remove(string problemId) => _planning.TryRemove(problemId, out _);

    /// <summary>The rows of one validation, shown as being planned until the returned scope ends, however the validation ends (placed, refused, failed or cancelled).</summary>
    public IDisposable Track(IEnumerable<PlannedRow> rows)
    {
        var ids = new List<string>();
        foreach (var row in rows)
        {
            Add(row);
            ids.Add(row.ProblemId);
        }
        return new Scope(this, ids);
    }

    private sealed class Scope(ProblemPlanning planning, List<string> ids) : IDisposable
    {
        public void Dispose()
        {
            foreach (var id in ids)
            {
                planning.Remove(id);
            }
        }
    }

    public IReadOnlyList<PlannedRow> All() => [.. _planning.Values.OrderBy(row => row.Since)];
}
