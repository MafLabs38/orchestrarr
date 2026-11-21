using System.Security.Cryptography;
using System.Text;
using Orchestrarr.Core.Decisions;

namespace Orchestrarr.Core.Missions;

/// <summary>
/// Makes the plan entry of a problem that the maintainer ticked (FUNCTIONAL 5.2): what the problem is, the one repository, how far it may go, the budget, and, when it is a pull request, the pull request and
/// the head commit that the maintainer saw. A row with no repository is no problem that can be started.
/// </summary>
public static class PlanEntryFactory
{
    /// <summary>The identifier of a problem: stable for a row (the same row is the same problem), plain text, short.</summary>
    public static string ProblemIdOf(string rowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rowId);
        return "p-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rowId)))[..12].ToLowerInvariant();
    }

    /// <summary>Null when the row names no repository (a problem is on exactly one, ADR 0041) or no application.</summary>
    public static PlanEntry? From(ProblemRow row, string approver, DateTimeOffset at, World maxWorld, MissionBudget budget)
    {
        ArgumentNullException.ThrowIfNull(row);
        var repository = row.Source ?? row.Application;
        if (!OrderValidation(repository))
        {
            return null;
        }
        var application = row.ApplicationId ?? repository[(repository.IndexOf('/', StringComparison.Ordinal) + 1)..];
        var objective = row.PullRequest is { } number
            ? $"Prove pull request #{number} of {repository} ({row.Description}) in staging, then merge it and verify the release."
            : $"Solve the problem \"{row.Description}\" ({row.Kind}) of {repository}.";
        return new PlanEntry(ProblemIdOf(row.Id), application, row.Kind.ToString(), objective, [repository], maxWorld, budget, new Validation(approver, null), at, row.PullRequest, row.HeadSha);
    }

    private static bool OrderValidation(string repository) => Orchestrarr.Core.Launch.OrderValidation.IsRepositoryName(repository);
}
