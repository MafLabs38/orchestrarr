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
/// The power in effect for a step is the **intersection** of the plan entry, the per-application and per-environment grant (D-10, D-11, D-53: off until the maintainer enables it) and the
/// standing rule when the validation comes from one: the lowest of their maximum worlds (ADR 0038, FR-SAF-12). **A grant that is not given is no power at all**; a rule that does not
/// apply (a validation by a person) is simply not part of the intersection.
/// </summary>
public static class EffectivePower
{
    public static World? Of(World planEntry, World? grant, World? rule, bool byRule = false)
    {
        if (grant is null || (byRule && rule is null))
        {
            return null;
        }
        var lowest = planEntry < grant ? planEntry : grant.Value;
        return byRule && rule < lowest ? rule : lowest;
    }

    /// <summary>Whether a world may be entered with the powers given.</summary>
    public static bool Reaches(World world, World planEntry, World? grant, World? rule, bool byRule = false) => Of(planEntry, grant, rule, byRule) is { } highest && world <= highest;
}
