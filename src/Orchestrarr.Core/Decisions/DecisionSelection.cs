using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Decisions;

/// <summary>A row as the operator chose it: what they saw (the commit and how far it went) and the switch.</summary>
public sealed record ChosenRow(string? Id, string? HeadSha, Ceiling? GoesUpTo, bool StopBeforeProduction, bool TryRepair = false);

/// <summary>What became of a selection against the current table: the problems to approve, or the first row that is not what the operator saw.</summary>
public sealed record SelectionResolution(IReadOnlyList<SelectedProblem> Selection, ProblemRow? Mismatch, bool Refused)
{
    public static SelectionResolution Ok(IReadOnlyList<SelectedProblem> selection) => new(selection, null, false);

    public static SelectionResolution Moved(ProblemRow? row) => new([], row, true);
}

/// <summary>
/// The rule that turns the rows an operator chose into what is approved (ADR 0019), once, in Core: the plan comes from the <b>current</b> table, never from the
/// client, and a row that is gone, has no action, moved to another commit, goes higher or lower than the operator saw, or was computed from other
/// descriptions is refused.
/// </summary>
public static class DecisionSelection
{
    public static SelectionResolution Resolve(IReadOnlyDictionary<string, ProblemRow> table, IReadOnlyList<ChosenRow> chosen, string? seenDescriptions, string currentDescriptions)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(currentDescriptions);
        var selection = new List<SelectedProblem>();
        var sameDescriptions = string.Equals(seenDescriptions ?? string.Empty, currentDescriptions, StringComparison.Ordinal);
        foreach (var row in chosen)
        {
            ProblemRow? current = null;
            if (row.Id is not null)
            {
                table.TryGetValue(row.Id, out current);
            }
            if (!sameDescriptions || current is null || current.PullRequest is null || current.GoesUpTo == Ceiling.NoAction
                || !string.Equals(current.HeadSha, row.HeadSha, StringComparison.Ordinal) || row.GoesUpTo != current.GoesUpTo)
            {
                return SelectionResolution.Moved(current);
            }
            // A repair that is not offered on the row now is a row that is not what the operator saw
            if (row.TryRepair && !current.CanRepair)
            {
                return SelectionResolution.Moved(current);
            }
            var stops = row.StopBeforeProduction && current.CanStopBeforeProduction;
            IReadOnlyList<StepKind> steps = row.TryRepair ? [StepKind.Repair] : [.. DecisionTable.StepsOf(current.GoesUpTo)];
            selection.Add(new SelectedProblem(current.WorkItemId, current.PullRequest.Value, current.HeadSha!, steps,
                $"goesUpTo={current.GoesUpTo} stopBeforeProduction={stops} repair={row.TryRepair} descriptions={(currentDescriptions.Length == 0 ? "none" : currentDescriptions)}"));
        }
        return SelectionResolution.Ok(selection);
    }
}
