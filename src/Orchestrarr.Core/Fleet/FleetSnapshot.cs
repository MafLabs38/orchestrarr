namespace Orchestrarr.Core.Fleet;

/// <summary>Alerts of one kind on a repository, by severity.</summary>
public sealed record AlertCounts(int Critical, int High, int Medium, int Low)
{
    public static AlertCounts None { get; } = new(0, 0, 0, 0);

    public int Total => Critical + High + Medium + Low;
}

/// <summary>Whether a scan of alerts is on, off, or could not be read: an off scan is not a clean repository.</summary>
public enum AlertScanState
{
    Unknown,
    Off,
    On,
}

public sealed record AlertScan(AlertScanState State, AlertCounts Counts)
{
    public static AlertScan Clean { get; } = new(AlertScanState.On, AlertCounts.None);

    public static AlertScan Of(AlertCounts counts) => new(AlertScanState.On, counts);
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
    bool Fork,
    AlertScan DependencyAlerts,
    AlertScan CodeScanningAlerts,
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
    IReadOnlyList<Placement> Placements,
    IReadOnlyList<string>? AppIds = null);

/// <summary>An application of the fleet as the source names it: what a person calls it, and the name of its icon (the source serves the picture).</summary>
public sealed record ApplicationInfo(string Id, string Name, string? Icon);

/// <summary>What a source says about the fleet at one moment, whatever the source (Portarr, an environment file…).</summary>
public sealed record FleetSnapshot(
    DateTimeOffset ReadAt, IReadOnlyList<RepositoryState> Repositories, IReadOnlyList<ImageState> Images, IReadOnlyList<ApplicationInfo>? Applications = null);

/// <summary>A picture of an application and its media type.</summary>
public sealed record IconImage(byte[] Content, string ContentType);

/// <summary>Where the icons of the applications come from (the source of the fleet serves them). A source without icons answers nothing.</summary>
public interface IIconSource
{
    /// <summary>The picture of the icon of this name, or null when there is none or it cannot be read.</summary>
    Task<IconImage?> GetAsync(string name, CancellationToken cancellationToken);
}

public sealed class NoIconSource : IIconSource
{
    public Task<IconImage?> GetAsync(string name, CancellationToken cancellationToken) => Task.FromResult<IconImage?>(null);
}

public static class FleetSourceKinds
{
    public const string Portarr = "portarr";
    public const string Sample = "sample";
    public const string None = "none";
}

/// <summary>A source of findings and of the environment description (SPEC D-03, D-18).</summary>
public interface IFleetSource
{
    /// <summary>The kind of source, shown in the status page: see <see cref="FleetSourceKinds"/>.</summary>
    string Kind { get; }

    /// <summary>False for the source that reads nothing: the screens say that no source is configured.</summary>
    bool Configured { get; }

    Task<FleetSnapshot> ReadAsync(CancellationToken cancellationToken);
}
