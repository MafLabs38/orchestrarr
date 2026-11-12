using Orchestrarr.Core.Decisions;

namespace Orchestrarr.Host.Decisions;

/// <summary>
/// The <c>Relevance</c> section: what the rules that keep a row honest take for granted (ADR 0027). Every value has the default of <see cref="RelevancePolicy.Default"/>;
/// a list given in the configuration replaces the default one.
/// </summary>
public sealed class RelevanceOptions
{
    public const string SectionName = "Relevance";

    /// <summary>How many days without a push, with a red build, make a repository dormant (7 to 3650).</summary>
    public int? DormantAfterDays { get; set; }

    /// <summary>The namespaces whose images come with the platform and are updated with it (K3s: <c>kube-system</c>).</summary>
    public string[]? PlatformNamespaces { get; set; }

    /// <summary>The packages moved only to a release with an even major version (Node: an odd one is never a long-term release).</summary>
    public string[]? EvenMajorOnlyPackages { get; set; }

    public RelevancePolicy ToPolicy()
    {
        var days = DormantAfterDays ?? RelevancePolicy.Default.DormantAfterDays;
        if (days is < 7 or > 3650)
        {
            throw new InvalidOperationException("Relevance:DormantAfterDays must be 7 to 3650.");
        }
        return new RelevancePolicy(days, PlatformNamespaces ?? [.. RelevancePolicy.Default.PlatformNamespaces], EvenMajorOnlyPackages ?? [.. RelevancePolicy.Default.EvenMajorOnlyPackages]);
    }
}
