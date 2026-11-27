using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

/// <summary>The worlds after staging merge and release the change that staging made: their controller reads the justification its worker gave for each pipeline change (ADR 0057).</summary>
public sealed class StagingJustificationTests
{
    private static Mission Mission(string id, World world, MissionState state) =>
        new(id, "p-1", "example/shop", world, "x", new MissionFrame(["example/shop"], [], ""), new MissionBudget(30, 3, 50), state);

    private static Dossier Dossier(string missionId, string? justification = null) => new(missionId, "done", "why", [], [], false, null, "abc", justification);

    [Fact]
    public void The_git_world_reads_the_justification_of_the_latest_accepted_staging_mission_and_staging_keeps_its_own()
    {
        var missions = new InMemoryMissionStore();
        var dossiers = new InMemoryDossierStore();
        missions.Save(Mission("m-old", World.Staging, MissionState.Accepted));
        dossiers.Save(Dossier("m-old", "an older change"));
        missions.Save(Mission("m-staging", World.Staging, MissionState.Accepted));
        dossiers.Save(Dossier("m-staging", "p7: the filter left out src/"));
        missions.Save(Mission("m-refused", World.Staging, MissionState.Ended));
        dossiers.Save(Dossier("m-refused", "a change that was not accepted"));
        var git = Mission("m-git", World.Git, MissionState.Running);

        MissionDriver.WithStagingJustification(missions, dossiers, git, Dossier("m-git")).PipelineJustification.Should().Be("p7: the filter left out src/");
        MissionDriver.WithStagingJustification(missions, dossiers, Mission("m-new", World.Staging, MissionState.Running), Dossier("m-new")).PipelineJustification.Should().BeNull("staging is judged on what its own worker said");
    }
}
