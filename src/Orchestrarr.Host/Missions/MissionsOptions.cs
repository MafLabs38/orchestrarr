using Orchestrarr.Core.Missions;

namespace Orchestrarr.Host.Missions;

/// <summary>The settings of the missions (ADR 0030 to 0032, 0036): which runner serves each world, how long a mandate lasts, the bounds of the loop.</summary>
public sealed class MissionsOptions
{
    public const string SectionName = "Missions";

    /// <summary>The runner of the staging world, of the Git world and of the release world. A mandate is for one runner and one world.</summary>
    public string StagingRunner { get; set; } = "runner-staging";

    public string GitRunner { get; set; } = "runner-git";

    public string ReleaseRunner { get; set; } = "runner-release";

    public int MandateMinutes { get; set; } = 60;

    public int MaxSendBacksPerMission { get; set; } = 3;

    public int MaxMandatesPerProblem { get; set; } = 8;

    public int MaxHoursPerProblem { get; set; } = 6;

    /// <summary>Where the private key that signs the mandates is kept. Unset: next to the database when there is one, else a key that lives as long as the host.</summary>
    public string? SigningKeyFile { get; set; }

    public ConductorOptions Conductor() => new(
        TimeSpan.FromMinutes(Math.Max(1, MandateMinutes)),
        ConductorOptions.Default.ForbiddenPaths,
        new LoopPolicy(Math.Max(1, MaxSendBacksPerMission), Math.Max(1, MaxMandatesPerProblem), TimeSpan.FromHours(Math.Max(1, MaxHoursPerProblem))));
}
