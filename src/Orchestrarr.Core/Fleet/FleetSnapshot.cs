namespace Orchestrarr.Core.Fleet;

/// <summary>Alerts of one kind on a repository, by severity.</summary>
public sealed record AlertCounts(int Critical, int High, int Medium, int Low)
{
    public static AlertCounts None { get; } = new(0, 0, 0, 0);

    public int Total => Critical + High + Medium + Low;
}

public enum CiState
{
    Unknown,
    Passing,
    Failing,
    Running,
}

public enum ImageStatus
{
    Unknown,
    UpToDate,
    Behind,
    NewerBuild,
    Floating,
    Pending,
}

public enum ImageChange
{
    None,
    Build,
    Patch,
    Minor,
    Major,
}

/// <summary>Where a workload runs: the environment is what a deployment targets.</summary>
public sealed record Placement(string EnvironmentId, string EnvironmentName, string? Namespace, string WorkloadName);

/// <summary>A code repository of the fleet and what is wrong with it.</summary>
public sealed record RepositoryState(
    string FullName,
    Uri? Url,
    bool Archived,
    AlertCounts DependencyAlerts,
    AlertCounts CodeScanningAlerts,
    int SecretScanningAlerts,
    int OpenDependencyPullRequests,
    CiState Ci,
    IReadOnlyList<string> AppIds,
    IReadOnlyList<string> ImageReferences);

/// <summary>A running image compared with its registry.</summary>
public sealed record ImageState(
    string Reference,
    string? Current,
    string? Latest,
    ImageStatus Status,
    ImageChange Change,
    string? RepositoryFullName,
    IReadOnlyList<Placement> Placements);

/// <summary>What a source of findings says about the fleet at one moment, whatever the source (Portarr, an environment file…).</summary>
public sealed record FleetSnapshot(DateTimeOffset ReadAt, IReadOnlyList<RepositoryState> Repositories, IReadOnlyList<ImageState> Images);

/// <summary>A source of findings and of the environment description (SPEC D-03, D-18).</summary>
public interface IFleetSource
{
    /// <summary>The kind of source, shown in the status page: <c>portarr</c>, <c>sample</c>…</summary>
    string Kind { get; }

    Task<FleetSnapshot> ReadAsync(CancellationToken cancellationToken);
}
