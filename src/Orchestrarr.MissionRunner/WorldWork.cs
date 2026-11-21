using Orchestrarr.Core.Missions;

namespace Orchestrarr.MissionRunner;

/// <summary>What a runner does in one world: takes the mission it claimed, works, reports and answers with the dossier. The worker around it starts the mission, watches the global stop and hands the dossier over.</summary>
public interface IWorldWork
{
    /// <summary>The world it serves (<c>Staging</c>, <c>Git</c> or <c>Release</c>).</summary>
    string World { get; }

    Task<MissionHandOver> RunAsync(ClaimedMission mission, Func<string, Task> report, CancellationToken cancellationToken);
}
