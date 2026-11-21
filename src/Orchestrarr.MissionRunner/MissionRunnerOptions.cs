using System.Text.RegularExpressions;

namespace Orchestrarr.MissionRunner;

/// <summary>The settings of a runner that serves the missions of one world (<c>MissionRunner</c> section).</summary>
public sealed partial class MissionRunnerOptions
{
    public const string SectionName = "MissionRunner";

    /// <summary>The name that its token carries in the control plane's <c>Tasks:Runners</c> and that its mandates name.</summary>
    public string Name { get; set; } = "runner-staging";

    public Uri? ControlPlaneUrl { get; set; }

    public string? Token { get; set; }

    public string? TokenFile { get; set; }

    /// <summary>The world this runner serves. This first version serves the staging world.</summary>
    public string World { get; set; } = "Staging";

    /// <summary>The SSH alias of the staging machine, and the name that the lab tool knows it by. Required: a runner that is not told which machine to restore does not guess one.</summary>
    public string? StagingMachine { get; set; }

    /// <summary>The tool that puts a staging machine back to its reference (<c>lab.sh</c>): called as <c>tool reset machine</c>.</summary>
    public string? LabTool { get; set; }

    /// <summary>The repositories whose pull requests this runner accepts (patterns like <c>owner/name</c> or <c>owner/*</c>): the control plane shows a problem as actionable only for them.</summary>
    public string[] Repositories { get; set; } = [];

    public int PollSeconds { get; set; } = 3;

    /// <summary>How often the global stop is read while a mission runs.</summary>
    public int StopPollSeconds { get; set; } = 5;

    public int StepTimeoutMinutes { get; set; } = 15;

    /// <summary>What a runner cannot start without: where the control plane is, its token, and the machine it works on. Throws with the name of the setting that is missing.</summary>
    public void Validate()
    {
        _ = ControlPlaneUrl ?? throw new InvalidOperationException("MissionRunner:ControlPlaneUrl is required.");
        _ = TokenValue();
        if (string.IsNullOrWhiteSpace(StagingMachine))
        {
            throw new InvalidOperationException("MissionRunner:StagingMachine is required (the SSH alias of the machine to restore and work on).");
        }
        if (!StagingMachineShape().IsMatch(StagingMachine))
        {
            throw new InvalidOperationException("MissionRunner:StagingMachine must be an SSH alias: letters, digits, dot, underscore and dash only.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.None, 250)]
    private static partial Regex StagingMachineShape();

    public string TokenValue()
    {
        if (!string.IsNullOrWhiteSpace(Token))
        {
            return Token;
        }
        if (TokenFile is { Length: > 0 } file)
        {
            return File.ReadAllText(file).Trim();
        }
        throw new InvalidOperationException("MissionRunner:Token or MissionRunner:TokenFile is required.");
    }
}
