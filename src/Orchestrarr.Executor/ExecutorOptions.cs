using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Executor;

/// <summary>Where the executor connects, and who it is. The token is a secret: it comes from the environment or a file, never from a committed setting.</summary>
public sealed record ControlPlaneOptions
{
    public string Url { get; init; } = string.Empty;

    public string Token { get; init; } = string.Empty;

    /// <summary>A file that holds the token (one line), for a deployment that mounts secrets as files. Read when <see cref="Token"/> is empty.</summary>
    public string TokenFile { get; init; } = string.Empty;
}

/// <summary>
/// What this executor accepts to do, whatever a task says (ADR 0017). It belongs to the executor's own configuration and the control plane
/// cannot change it. Empty means nothing: a task kind or a repository that is not listed is refused.
/// </summary>
public sealed record RightsOptions
{
    public IReadOnlyList<TaskKind> Kinds { get; init; } = [];

    /// <summary>Repositories as <c>owner/name</c>; a <c>*</c> stands for any characters (<c>example/*</c>, <c>*</c>).</summary>
    public IReadOnlyList<string> Repositories { get; init; } = [];

    /// <summary>
    /// Whether this executor runs a batch that cites one of its standing delegations instead of a code. Off by default: nothing in the control
    /// plane makes such a batch yet, and a path nobody uses is a path a taken-over control plane could try. Turned on with the flow that uses it.
    /// </summary>
    public bool AcceptDelegations { get; init; }
}

public sealed record ExecutorOptions
{
    public const string SectionName = "Executor";

    /// <summary>The name this executor gives itself, in the trace and in the screen. The control plane knows it by its token.</summary>
    public string Name { get; init; } = Environment.MachineName;

    public ControlPlaneOptions ControlPlane { get; init; } = new();

    public RightsOptions Rights { get; init; } = new();

    /// <summary>The real validation of pull requests. Not configured: validating stays a dry run.</summary>
    public ValidationOptions Validation { get; init; } = new();

    /// <summary>The agent that tries to repair a pull request that fails. Not configured: the task is not offered by this executor.</summary>
    public RepairOptions Repair { get; init; } = new();

    /// <summary>The executor's own file: the operators' secrets, the codes used, the batches run, the delegations and the trace of the runs.</summary>
    public string DataPath { get; init; } = "executor.db";

    /// <summary>Where the control plane writes the journal of approvals, for the double control. Empty: no double control is possible and every batch is refused.</summary>
    public string JournalPath { get; init; } = string.Empty;

    /// <summary>
    /// A file whose presence stops this executor, whatever the control plane says: it asks for nothing while the file is there, and a run ends
    /// before its next task. Unset: a file named <c>STOP</c> in the folder of <see cref="DataPath"/>, so that the local stop is never off.
    /// </summary>
    public string LocalStopFile { get; init; } = string.Empty;

    /// <summary>The local stop file in force: the one given, else <c>STOP</c> next to the executor's own file.</summary>
    public string StopFile => LocalStopFile.Length > 0 ? LocalStopFile : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(DataPath)) ?? ".", "STOP");

    public bool IsLocallyStopped() => File.Exists(StopFile);

    /// <summary>
    /// Whether this executor checks the code of the authenticator. On by default. It can be turned off here only, by the person who runs the executor
    /// (the control plane cannot ask for it), and the executor then refuses to start unless every runner is a dry run (ADR 0023).
    /// </summary>
    public bool RequireSecondFactor { get; init; } = true;

    public int PollSeconds { get; init; } = 3;

    /// <summary>The name shown in the authenticator app next to the account.</summary>
    public string Issuer { get; init; } = "Orchestrarr";
}
