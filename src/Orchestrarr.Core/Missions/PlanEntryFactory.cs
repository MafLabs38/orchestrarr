using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Orchestrarr.Core.Decisions;

namespace Orchestrarr.Core.Missions;

/// <summary>
/// Makes the plan entry of a problem that the maintainer ticked (FUNCTIONAL 5.2): what the problem is, the one repository, how far it may go, the budget, and, when it is a pull request, the pull request and
/// the head commit that the maintainer saw. A row with no repository is no problem that can be started.
/// </summary>
public static partial class PlanEntryFactory
{
    // The sentence of the objective is read by the controller as the control plane's own. A pull request's title is text of the forge, so it stays out of it:
    // only what the control plane parsed from it (package, from, to) is a statement of its own, and it is still checked to be plain characters.
    [GeneratedRegex(@"^[A-Za-z0-9@/._\-]{1,100} [A-Za-z0-9.+_\-]{1,50} → [A-Za-z0-9.+_\-]{1,50}\z", RegexOptions.CultureInvariant, 250)]
    private static partial Regex PlainChange();

    private static string Described(ProblemRow row) => row.ParsedChange is { } change && PlainChange().IsMatch(change) ? change : "a change described by the forge";

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
            ? $"Prove pull request #{number} of {repository} ({Described(row)}) in staging: build it and run its tests there. Merging and releasing are the work of the next worlds. If it does not pass as it is, the worker agent may repair the source so that it does, as a developer would: the source, and when the bump needs it a companion dependency (a library that requires a newer version of another), the build, the container or the pipeline; never the tests, which are what the change is judged on. That repair is expected and is part of the objective, and it is carried with the change."
            : $"Solve the problem of kind {row.Kind} of {repository}.";
        return new PlanEntry(ProblemIdOf(row.Id), application, row.Kind.ToString(), objective, [repository], maxWorld, budget, new Validation(approver, null), at, row.PullRequest, row.HeadSha);
    }

    private static bool OrderValidation(string repository) => Orchestrarr.Core.Launch.OrderValidation.IsRepositoryName(repository);
}
