using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Host.Approvals;

/// <summary>The powers of the configuration: <c>Powers:Grants</c>, a list of repository and steps. Empty: nothing is granted.</summary>
public sealed class PowersOptions
{
    public const string SectionName = "Powers";

    public List<PowerGrantOptions> Grants { get; set; } = [];

    /// <summary>
    /// The grants, checked when the host starts, not at the first request: a repository is <c>owner/name</c> or <c>*</c>, a grant lists known
    /// steps, and outside the Development environment a grant on every repository may only cover validating (a merge is granted
    /// repository by repository).
    /// </summary>
    public IReadOnlyList<PowerGrant> ToGrants(bool isDevelopment)
    {
        var grants = new List<PowerGrant>();
        foreach (var grant in Grants)
        {
            var repository = grant.Repository?.Trim() ?? string.Empty;
            var wildcard = repository == PowerGrant.Wildcard;
            if (!wildcard && !Orchestrarr.Core.Launch.OrderValidation.IsRepositoryName(repository))
            {
                throw new InvalidOperationException($"Powers:Grants: '{repository}' is neither owner/name nor {PowerGrant.Wildcard}.");
            }
            if (grant.Steps.Count == 0 || grant.Steps.Any(step => !Enum.IsDefined(step)))
            {
                throw new InvalidOperationException($"Powers:Grants: the grant for '{repository}' lists no step, or an unknown one.");
            }
            if (wildcard && !isDevelopment && grant.Steps.Any(step => step != StepKind.Validate))
            {
                throw new InvalidOperationException("Powers:Grants: outside Development, a grant on every repository may only cover validate.");
            }
            grants.Add(new PowerGrant(repository, [.. grant.Steps]));
        }
        return grants;
    }
}
