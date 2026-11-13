namespace Orchestrarr.Core.Missions;

/// <summary>
/// A validation given in advance for a class of problems (FR-PLAN-4). It is **bounded** (FR-SAF-12): an expiry, a maximum world, the repositories it covers; and creating or
/// widening one needs the same second factor as an approval. A rule never widens a grant.
/// </summary>
public sealed record StandingRule(string Id, string ProblemClass, World MaxWorld, IReadOnlyList<string> Repositories, DateTimeOffset ExpiresAt, bool Revoked)
{
    public bool Covers(string problemClass, string repository, World world, DateTimeOffset now) =>
        !Revoked
        && now < ExpiresAt
        && string.Equals(ProblemClass, problemClass, StringComparison.Ordinal)
        && world <= MaxWorld
        && Repositories.Contains(repository, StringComparer.Ordinal);
}

/// <summary>
/// The power in effect for a step is the **intersection** of the plan entry, the per-application and per-environment grant (D-10, D-11, D-53) and the mandate: the lowest of
/// the maximum worlds (ADR 0038). A null maximum means "no power at all" for that source.
/// </summary>
public static class EffectivePower
{
    public static World? Of(World? planEntry, World? grant, World? rule)
    {
        World? lowest = null;
        foreach (var candidate in new[] { planEntry, grant, rule })
        {
            if (candidate is null)
            {
                continue;
            }
            lowest = lowest is null || candidate < lowest ? candidate : lowest;
        }
        return lowest;
    }

    /// <summary>Whether a world may be entered with the powers given: every source that exists must reach it, and at least one must exist.</summary>
    public static bool Reaches(World world, World? planEntry, World? grant, World? rule) => Of(planEntry, grant, rule) is { } highest && world <= highest;
}
