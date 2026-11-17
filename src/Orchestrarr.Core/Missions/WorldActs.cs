namespace Orchestrarr.Core.Missions;

/// <summary>
/// The acts that a mandate for a world may list: one table, read by the conductor that issues the mandate and by the runner that verifies it (rule 14), so that a mandate that lists an act of another world
/// (a Git-world mandate that lists <c>Deploy</c>) is refused whoever signed it.
/// </summary>
public static class WorldActs
{
    public static IReadOnlyList<GatedAct> Of(World world) => world switch
    {
        // The platform reserves, restores and gives back the staging slots, not the worker (ADR 0041 point 3): the staging world lists no act of a mandate
        World.Staging => [],
        World.Git => [GatedAct.OpenPullRequest, GatedAct.MergePullRequest],
        World.Release => [GatedAct.Deploy],
        _ => [],
    };
}
