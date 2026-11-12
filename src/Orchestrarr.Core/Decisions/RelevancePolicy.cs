namespace Orchestrarr.Core.Decisions;

/// <summary>
/// What the rules that keep a row honest take for granted, as the operator's settings (ADR 0027): how long without a push makes a repository dormant, the
/// namespaces whose images come with the platform and are updated with it, and the packages that are moved only to a release with an even major version (the
/// convention of Node: an odd version is never a long-term release).
/// </summary>
public sealed record RelevancePolicy(int DormantAfterDays, IReadOnlyList<string> PlatformNamespaces, IReadOnlyList<string> EvenMajorOnlyPackages)
{
    public static RelevancePolicy Default { get; } = new(120, ["kube-system"], ["node"]);
}
