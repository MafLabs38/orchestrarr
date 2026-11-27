namespace Orchestrarr.Core.Missions;

/// <summary>
/// The acts that a mandate for a world may list: one table, read by the conductor that issues the mandate and by the runner that verifies it (rule 14), so that a mandate that lists an act of another world
/// (a Git-world mandate that lists <c>Deploy</c>) is refused whoever signed it.
/// </summary>
public static class WorldActs
{
    public static IReadOnlyList<GatedAct> Of(World world) => world switch
    {
        // The platform reserves, restores and gives back the staging slots, not the worker (ADR 0041 point 3); the one act of the staging world is the push of a free agent's own branch, with a credential the control plane makes (ADR 0057)
        World.Staging => [GatedAct.PushBranch],
        World.Git => [GatedAct.OpenPullRequest, GatedAct.MergePullRequest],
        World.Release => [GatedAct.Deploy],
        _ => [],
    };

    /// <summary>The acts that a mandate lists for this operation in this world: a free agent pushes only for a problem that has no pull request (ADR 0048); the staging of a pull request pushes nothing.</summary>
    public static IReadOnlyList<GatedAct> Of(World world, PlanEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return world == World.Staging && entry.PullRequest is not null ? [] : Of(world);
    }
}
