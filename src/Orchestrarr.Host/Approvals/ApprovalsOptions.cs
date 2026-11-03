using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Host.Approvals;

public enum ApprovalsMode
{
    /// <summary>Nobody can approve: the interface shows the cards and says so.</summary>
    Disabled,

    /// <summary>A single, fixed operator with no sign-in, for the local sandbox only (see <see cref="SandboxGuard"/>).</summary>
    Development,
}

public sealed class ApprovalsOptions
{
    public const string SectionName = "Approvals";

    public ApprovalsMode Mode { get; set; } = ApprovalsMode.Disabled;

    /// <summary>The name the sandbox operator signs with.</summary>
    public string DevelopmentUser { get; set; } = "dev-operator";

    /// <summary>How many approvals the queue holds before it refuses (it never forgets one to make room).</summary>
    public int QueueCapacity { get; set; } = 500;

    /// <summary>How many entries the in-memory audit trail holds before it refuses.</summary>
    public int AuditCapacity { get; set; } = 2000;
}

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
            if (!wildcard && !SandboxGuard.IsRepositoryName(repository))
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

public sealed class PowerGrantOptions
{
    public string Repository { get; set; } = string.Empty;

    public List<StepKind> Steps { get; set; } = [];
}
