namespace Orchestrarr.Core.WorkItems;

/// <summary>One grant of the operator: these steps, on this repository (<c>owner/name</c>) or on every one (<c>*</c>).</summary>
public sealed record PowerGrant(string Repository, IReadOnlyList<StepKind> Steps)
{
    /// <summary>The repository of a grant that covers every repository.</summary>
    public const string Wildcard = "*";

    /// <summary>Whether this is a repository name, <c>owner/name</c>, in the shape that an order accepts.</summary>
    public static bool IsRepositoryName(string value) => Launch.OrderValidation.IsRepositoryName(value);
}

/// <summary>
/// The powers the operator granted in the configuration (SPEC D-10, D-11): off until listed. A grant covers steps of a repository; a step
/// on an environment (a deployment) is granted per environment, which comes with the vertical that deploys, so it is never granted here.
/// </summary>
public sealed class ConfiguredPowerPolicy(IEnumerable<PowerGrant> grants) : IPowerPolicy
{
    private readonly PowerGrant[] _grants = [.. grants];

    public bool IsGranted(StepKind step, string? repositoryFullName, string? environmentId) =>
        environmentId is null
        && repositoryFullName is not null
        && _grants.Any(grant => grant.Steps.Contains(step) && (grant.Repository == PowerGrant.Wildcard || string.Equals(grant.Repository, repositoryFullName, StringComparison.OrdinalIgnoreCase)));
}
