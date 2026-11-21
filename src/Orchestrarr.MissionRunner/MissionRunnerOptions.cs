using System.Text.RegularExpressions;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.MissionRunner;

/// <summary>The settings of a runner that serves the missions of some worlds (<c>MissionRunner</c> section).</summary>
public sealed partial class MissionRunnerOptions
{
    public const string SectionName = "MissionRunner";

    /// <summary>The name that its token carries in the control plane's <c>Tasks:Runners</c> and that its mandates name.</summary>
    public string Name { get; set; } = "runner-staging";

    public Uri? ControlPlaneUrl { get; set; }

    public string? Token { get; set; }

    public string? TokenFile { get; set; }

    /// <summary>The worlds this runner serves: the missions of these worlds whose mandate names it. A world it does not serve is never claimed (it would block the missions behind it).</summary>
    public string[] Worlds { get; set; } = ["Staging"];

    /// <summary>The SSH alias of the staging machine, and the name that the lab tool knows it by. Required: a runner that is not told which machine to restore does not guess one.</summary>
    public string? StagingMachine { get; set; }

    /// <summary>The tool that puts a staging machine back to its reference (<c>lab.sh</c>): called as <c>tool reset machine</c>.</summary>
    public string? LabTool { get; set; }

    /// <summary>The repositories whose pull requests this runner accepts (patterns like <c>owner/name</c> or <c>owner/*</c>): the control plane shows a problem as actionable only for them.</summary>
    public string[] Repositories { get; set; } = [];

    /// <summary>The SSH alias of the machine that a release deploys to (the release world). Required when the runner serves it; never the staging machine, which is restored after each job.</summary>
    public string? DeployMachine { get; set; }

    /// <summary>The address under which the deployed applications are reachable, only to say it in the dossier (default: the alias of the machine).</summary>
    public string? DeployHost { get; set; }

    /// <summary>The first port of the deployed applications: each one gets a stable port from here (the same repository always lands on the same port).</summary>
    public int DeployPortBase { get; set; } = DeploymentPort.DefaultBase;

    /// <summary>How long the Git world waits for a pipeline (the pull request's before the merge, the integration branch's after it), and how often it asks.</summary>
    public int PipelineWaitMinutes { get; set; } = 15;

    public int PipelineAskSeconds { get; set; } = 15;

    /// <summary>The agent that repairs a change that does not pass in staging (ADR 0044). Not configured: a change that does not pass is handed over as it is.</summary>
    public RepairSettings Repair { get; set; } = new();

    public int PollSeconds { get; set; } = 3;

    /// <summary>How often the global stop is read while a mission runs.</summary>
    public int StopPollSeconds { get; set; } = 5;

    /// <summary>How many reads of the stop in a row may fail before the work is stopped: a stop that cannot be confirmed for a while stops the work (about half a minute with the default poll).</summary>
    public int StopReadFailuresAllowed { get; set; } = 6;

    public int StepTimeoutMinutes { get; set; } = 15;

    /// <summary>What a runner cannot start without: where the control plane is, its token, and the machine it works on. Throws with the name of the setting that is missing.</summary>
    public void Validate()
    {
        _ = ControlPlaneUrl ?? throw new InvalidOperationException("MissionRunner:ControlPlaneUrl is required.");
        _ = TokenValue();
        if (string.IsNullOrWhiteSpace(LabTool))
        {
            throw new InvalidOperationException("MissionRunner:LabTool is required (the tool that restores the staging machine: <tool> reset <machine>).");
        }
        if (string.IsNullOrWhiteSpace(StagingMachine))
        {
            throw new InvalidOperationException("MissionRunner:StagingMachine is required (the SSH alias of the machine to restore and work on).");
        }
        if (!StagingMachineShape().IsMatch(StagingMachine))
        {
            throw new InvalidOperationException("MissionRunner:StagingMachine must be an SSH alias: letters, digits, dot, underscore and dash only.");
        }
        if (PipelineAskSeconds < 1 || PipelineWaitMinutes < 1 || StepTimeoutMinutes < 1 || !DeploymentPort.IsValidBase(DeployPortBase))
        {
            throw new InvalidOperationException("MissionRunner: PipelineAskSeconds, PipelineWaitMinutes and StepTimeoutMinutes must be at least 1, and DeployPortBase must leave room for 100 ports below 65535.");
        }
        Repair.Validate();
        if (Worlds.Length == 0)
        {
            throw new InvalidOperationException("MissionRunner:Worlds must name at least one world.");
        }
        if (Worlds.Contains("Release", StringComparer.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(DeployMachine) || !StagingMachineShape().IsMatch(DeployMachine) || string.Equals(DeployMachine, StagingMachine, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("MissionRunner:DeployMachine is required to serve the Release world: an SSH alias, and not the staging machine.");
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
