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

    public int MandateMinutes { get; set; } = (int)ConductorOptions.Default.MandateLifetime.TotalMinutes;

    public int MaxSendBacksPerMission { get; set; } = LoopPolicy.Default.MaxSendBacksPerMission;

    public int MaxMandatesPerProblem { get; set; } = LoopPolicy.Default.MaxMandatesPerProblem;

    public int MaxHoursPerProblem { get; set; } = (int)LoopPolicy.Default.MaxTimePerProblem.TotalHours;

    /// <summary>Where the private key that signs the mandates is kept. Unset: next to the database when there is one, else a key that lives as long as the host.</summary>
    public string? SigningKeyFile { get; set; }

    /// <summary>
    /// **Sandbox only, Development only** (the host refuses to start otherwise): an approval authority in this process with an account of its own and no second factor to type, the facts of GitHub for the pull request of a
    /// problem, and a frame that is declared to hold (merging does not deploy, tags and credentials are out of the agent's reach). It lets the test bed run a problem end to end; a deployment never sets it.
    /// </summary>
    public bool Sandbox { get; set; }

    /// <summary>With <see cref="Sandbox"/>: the highest world the test bed grants to every application. The Git world by default; the release world is granted by setting `Release`, which is what lets the test bed deploy.</summary>
    public World SandboxMaxWorld { get; set; } = World.Git;

    /// <summary>With <see cref="Sandbox"/>: the address of the machine that releases are deployed to, so that the control plane looks at the deployed application itself (<c>http://host:port/health</c>, the port being the repository's own, <see cref="Orchestrarr.Core.Missions.DeploymentPort"/>). Unset: the health of a release is not read.</summary>
    public string? SandboxDeployHost { get; set; }

    /// <summary>
    /// With <see cref="Sandbox"/>: the token with which the control plane's act gateway merges, **its own and with no fallback to <c>GitHub:Token</c>** (which reads), and the repositories it may merge on (patterns like <c>owner/name</c> or <c>owner/*</c>).
    /// Without both, nothing is merged.
    /// </summary>
    public string? SandboxMergeToken { get; set; }

    public string[] SandboxMergeRepositories { get; set; } = [];

    /// <summary>The first port of the deployed applications. It must be the same value as <c>MissionRunner:DeployPortBase</c>, or the control plane probes another port than the one deployed to.</summary>
    public int SandboxDeployPortBase { get; set; } = DeploymentPort.DefaultBase;

    /// <summary>The paths no mission may touch, whatever its repository (pipelines, secrets). Unset: the pipelines of the forge, the encrypted files and the environment files.</summary>
    public string[]? ForbiddenPaths { get; set; }

    /// <summary>The public key of the approval authority (ADR 0031 point 7), a file in SubjectPublicKeyInfo form. Unset: no certificate is ever valid, so no real mandate is issued.</summary>
    public string? ApprovalAuthorityPublicKeyFile { get; set; }

    /// <summary>The settings of the conductor. A bound that is not a positive number is refused at start, not corrected: a loop with no bound is the mistake to catch.</summary>
    public ConductorOptions Conductor()
    {
        Positive(MandateMinutes, nameof(MandateMinutes));
        Positive(MaxSendBacksPerMission, nameof(MaxSendBacksPerMission));
        Positive(MaxMandatesPerProblem, nameof(MaxMandatesPerProblem));
        Positive(MaxHoursPerProblem, nameof(MaxHoursPerProblem));
        return new ConductorOptions(
            TimeSpan.FromMinutes(MandateMinutes),
            ForbiddenPaths ?? [.. ConductorOptions.Default.ForbiddenPaths],
            new LoopPolicy(MaxSendBacksPerMission, MaxMandatesPerProblem, TimeSpan.FromHours(MaxHoursPerProblem)));
    }

    private static void Positive(int value, string name)
    {
        if (value < 1)
        {
            throw new InvalidOperationException($"{SectionName}:{name} must be a positive number, not {value}.");
        }
    }
}
