namespace Orchestrarr.Executor;

/// <summary>One command of a validation, run in the container of its profile. The command is the executor's own configuration: a task never carries one.</summary>
public sealed record ValidationStep
{
    public string Name { get; init; } = string.Empty;

    public string Command { get; init; } = string.Empty;

    /// <summary>Whether the container may reach the network. Off by default: the install step turns it on, the tests do not need it.</summary>
    public bool Network { get; init; }
}

/// <summary>How the repositories matching the patterns are validated: the image they run in and the steps, in order.</summary>
public sealed record ValidationProfile
{
    public IReadOnlyList<string> Repositories { get; init; } = [];

    public string Image { get; init; } = string.Empty;

    public IReadOnlyList<ValidationStep> Steps { get; init; } = [];
}

/// <summary>
/// The real validation of a pull request (ADR 0017: the first task that runs something). The pull request is fetched into a fresh folder, checked
/// against the commit that was approved, and its steps run in a container with no credential, a bounded CPU, memory and time. Nothing is written
/// back to the repository. No profile: no validation, and the task is refused.
/// </summary>
public sealed record ValidationOptions
{
    /// <summary>
    /// This executor runs on a host that is a workshop: it may run the code of pull requests in containers, from a user that can start any container. It is
    /// an explicit choice (D-25: the workshop is apart from the target environments), never a default; without it the validation is not configured.
    /// </summary>
    public bool Workshop { get; init; }

    /// <summary>Where the folders of the runs are made (and removed). Empty: validation is not configured and the dry run stays.</summary>
    public string WorkRoot { get; init; } = string.Empty;

    /// <summary>Where a repository is fetched from; <c>{0}</c> is the full name (owner/name).</summary>
    public string RepositoryUrl { get; init; } = "https://github.com/{0}.git";

    /// <summary>
    /// A file that holds a token to fetch with. Read-only access is enough and is all this executor should hold. It goes to the <c>git</c> process
    /// only: never to the container, never to the trace. Empty: git's own credentials (a public repository needs none).
    /// </summary>
    public string GitTokenFile { get; init; } = string.Empty;

    public int StepTimeoutMinutes { get; init; } = 10;

    public double Cpus { get; init; } = 1;

    public int MemoryMegabytes { get; init; } = 1024;

    /// <summary>The most processes a container may start.</summary>
    public int PidsLimit { get; init; } = 512;

    /// <summary>The size of the temporary folder of a container.</summary>
    public int TmpfsMegabytes { get; init; } = 512;

    /// <summary>The user the container runs as, <c>uid:gid</c>: the one that owns the work folder, never root.</summary>
    public string ContainerUser { get; init; } = "1000:1000";

    /// <summary>The size of the end of a command's output that is kept in the trace.</summary>
    public int MaxOutputCharacters { get; init; } = 3000;

    public IReadOnlyList<ValidationProfile> Profiles { get; init; } = [];

    public bool IsConfigured => Workshop && WorkRoot.Length > 0 && Profiles.Count > 0;
}
