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

    public bool IsConfigured => !string.IsNullOrWhiteSpace(DockerfileFile) && !string.IsNullOrWhiteSpace(TokenFile);

    /// <summary>The values that go into a script or a command line are checked here, so that a setting cannot carry anything but a name.</summary>
    public void Validate()
    {
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
}
