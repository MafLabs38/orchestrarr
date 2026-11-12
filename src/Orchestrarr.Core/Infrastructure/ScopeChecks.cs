namespace Orchestrarr.Core.Infrastructure;

/// <summary>What the description says against what the fleet says (ADR 0028): the identifiers of environments are Portarr's, and nothing else keeps them honest.</summary>
public static class ScopeChecks
{
    /// <summary>
    /// The environments named by the description (its list, its repositories, its platforms) that no workload of the fleet is in: a typo, or an environment that Portarr
    /// names otherwise. Their places would be left out of every row, so the table says so instead of leaving the cause unseen. Nothing is said when the fleet is empty.
    /// </summary>
    public static IReadOnlyList<RepositoryProblem> UnknownEnvironments(DescriptionScope scope, IEnumerable<string> fleetEnvironments)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(fleetEnvironments);
        var known = fleetEnvironments.ToHashSet(StringComparer.Ordinal);
        if (known.Count == 0)
        {
            return [];
        }
        var named = scope.Environments.Select(environment => environment.Id)
            .Concat(scope.Repositories.Select(repository => repository.Environment).OfType<string>())
            .Concat(scope.Platforms.Select(platform => platform.Environment).OfType<string>())
            .Distinct(StringComparer.Ordinal);
        return [.. named.Where(id => !known.Contains(id)).Select(id => new RepositoryProblem($"environment {id}", "no workload of the fleet is in it: Portarr names its environments otherwise"))];
    }
}
