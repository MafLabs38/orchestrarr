namespace Orchestrarr.MissionRunner;

/// <summary>The settings of the worker agent that repairs a change in the staging world (<c>MissionRunner:Repair</c>): Claude Code, in a container of the staging machine, under the subscription of the maintainer.</summary>
public sealed class RepairSettings
{
    /// <summary>The Dockerfile of the agent's image (<c>docker/agent/Dockerfile</c>), read here and built on the staging machine when it has no image yet: the machine is restored after each job, so what it holds is built again.</summary>
    public string? DockerfileFile { get; set; }

    /// <summary>A file with the token of the subscription (<c>CLAUDE_CODE_OAUTH_TOKEN</c>). It goes to the agent's container by the standard input of the connection, never in an argument, a script or a trace.</summary>
    public string? TokenFile { get; set; }

    public string Image { get; set; } = "orchestrarr-agent";

    public string Model { get; set; } = "sonnet";

    public int MaxTurns { get; set; } = 20;

    /// <summary>How many times the agent tries: after a try the steps are run again, and the next try is told what still fails.</summary>
    public int Attempts { get; set; } = 2;

    public int TimeoutMinutes { get; set; } = 15;

    /// <summary>The Dockerfile of the free agent's image (<c>docker/agent-free/Dockerfile</c>), for a problem that has no pull request (ADR 0048). Unset: such a problem is refused by this runner.</summary>
    public string? FreeDockerfileFile { get; set; }

    public string FreeImage { get; set; } = "orchestrarr-agent-free";

    /// <summary>Where the free agent's clone comes from: <c>{0}</c> is the repository (owner/name). Plain https, no credential in it.</summary>
    public string FreeRepositoryUrl { get; set; } = "https://github.com/{0}.git";

    public int FreeMaxTurns { get; set; } = 120;

    /// <summary>How long the free agent may work. Generous: a long troubleshooting is normal, and the machine is its own.</summary>
    public int FreeTimeoutMinutes { get; set; } = 60;

    /// <summary>A file that holds the token that lets the free agent push branches to the forge (and nothing else: no pull request, no workflow, no administration; the default branch refuses direct pushes by its own rules).</summary>
    public string? FreeGitTokenFile { get; set; }

    public bool IsFreeConfigured => !string.IsNullOrWhiteSpace(FreeDockerfileFile) && !string.IsNullOrWhiteSpace(TokenFile) && !string.IsNullOrWhiteSpace(FreeGitTokenFile);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(DockerfileFile) && !string.IsNullOrWhiteSpace(TokenFile);

    /// <summary>The values that go into a script or a command line are checked here, so that a setting cannot carry anything but a name.</summary>
    public void Validate()
    {
        ValidateFree();
        if (!IsConfigured)
        {
            return;
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(Image, "^[a-z0-9][a-z0-9._/-]{0,80}$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(250))
            || !System.Text.RegularExpressions.Regex.IsMatch(Model, "^[A-Za-z0-9._-]{1,40}$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(250)))
        {
            throw new InvalidOperationException("MissionRunner:Repair: the image and the model must be plain names.");
        }
        if (MaxTurns is < 1 or > 100 || Attempts is < 1 or > 5 || TimeoutMinutes is < 1 or > 60 || !File.Exists(DockerfileFile) || !File.Exists(TokenFile))
        {
            throw new InvalidOperationException("MissionRunner:Repair: the turns (1 to 100), the attempts (1 to 5) and the time (1 to 60 minutes) must be in range, and the Dockerfile and the token file must exist.");
        }
    }

    // The free agent has its own settings: a plain image name, a number of turns and a time in range, and the files that it names
    private void ValidateFree()
    {
        if (!IsFreeConfigured)
        {
            return;
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(FreeImage, "^[a-z0-9][a-z0-9._/-]{0,80}$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(250))
            || !System.Text.RegularExpressions.Regex.IsMatch(Model, "^[A-Za-z0-9._-]{1,40}$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(250)))
        {
            throw new InvalidOperationException("MissionRunner:Repair: the free agent's image and the model must be plain names.");
        }
        if (!FreeRepositoryUrl.StartsWith("https://", StringComparison.Ordinal) || !FreeRepositoryUrl.Contains("{0}", StringComparison.Ordinal) || FreeRepositoryUrl.Contains('@', StringComparison.Ordinal) || FreeRepositoryUrl.Any(char.IsWhiteSpace) || FreeRepositoryUrl.IndexOfAny(['\'', '"', '`', '$', ';', '&', '|']) >= 0)
        {
            throw new InvalidOperationException("MissionRunner:Repair:FreeRepositoryUrl must be a plain https address with {0} for the repository and no credential.");
        }
        if (FreeMaxTurns is < 1 or > 1000 || FreeTimeoutMinutes is < 1 or > 600 || !File.Exists(FreeDockerfileFile) || !File.Exists(TokenFile) || !File.Exists(FreeGitTokenFile))
        {
            throw new InvalidOperationException("MissionRunner:Repair: the free agent's turns (1 to 1000) and time (1 to 600 minutes) must be in range, and its Dockerfile, the token file and the file of the git token must exist.");
        }
    }
}
