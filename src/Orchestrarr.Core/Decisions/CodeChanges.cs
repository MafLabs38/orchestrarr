using Orchestrarr.Core.Infrastructure;

namespace Orchestrarr.Core.Decisions;

/// <summary>A change to a file of the scope, computed and not applied: the line before and after, and the unified diff.</summary>
public sealed record CodeChange(string Repository, string Path, int Line, string Before, string After, string Diff);

/// <summary>What changing the version of a row would write: where, with what, and the places that are not ours to change.</summary>
public sealed record CodeChangeReport(string RowId, string? Tag, IReadOnlyList<CodeChange> Changes, IReadOnlyList<CodeLocation> Left);

/// <summary>
/// The change that a row proposes, computed from the text that was read (ADR 0028). It lives in Core because the Runner will need the same computation when a change
/// is applied (rule 14); here nothing is written.
/// </summary>
public static class CodeChanges
{
    /// <summary>Null when the row proposes no change (no place, or no version to write).</summary>
    public static CodeChangeReport? For(ProblemRow row, InfrastructureIndex index)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(index);
        if (row.ProposedTag is not { } tag || row.Locations is not { Count: > 0 } locations)
        {
            return null;
        }
        var changes = new List<CodeChange>();
        var left = new List<CodeLocation>();
        foreach (var location in locations)
        {
            var change = index.Content(location.Repository, location.Path) is { } content ? VersionPatch.Replace(content, location, tag) : null;
            if (change is null)
            {
                left.Add(location);
                continue;
            }
            changes.Add(new CodeChange(location.Repository, location.Path, location.Line, change.Before, change.After, change.Diff));
        }
        return new CodeChangeReport(row.Id, tag, changes, left);
    }
}
