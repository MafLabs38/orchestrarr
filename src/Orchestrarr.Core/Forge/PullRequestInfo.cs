namespace Orchestrarr.Core.Forge;

/// <summary>Whether the checks of a pull request pass. Unknown: none ran, or none could be read.</summary>
public enum ChecksState
{
    Unknown,
    Pending,
    Passing,
    Failing,
}

/// <summary>Whether a pull request can be merged now, as the forge says.</summary>
public enum MergeState
{
    Unknown,
    Clean,

    /// <summary>Mergeable, but a check that is not required fails.</summary>
    Unstable,
    Behind,
    Blocked,
    Conflicting,
    Draft,
}

public enum UpdateKind
{
    Unknown,
    Patch,
    Minor,
    Major,
}

/// <summary>One dependency a pull request updates. Versions are known only for a pull request that updates a single one.</summary>
public sealed record DependencyChange(string Package, string? From, string? To, UpdateKind Kind);

public sealed record PullRequestInfo(
    int Number,
    string Title,
    Uri Url,
    string Author,
    string HeadBranch,
    bool Draft,
    ChecksState Checks,
    MergeState Merge,
    IReadOnlyList<string> Labels,
    IReadOnlyList<DependencyChange> Changes,
    DateTimeOffset CreatedAt);

/// <summary>A forge (GitHub first): where the pull requests of a repository are read. Read-only in this vertical.</summary>
public interface IPullRequestSource
{
    string Kind { get; }

    /// <summary>False for the source that reads nothing: the screens say the forge is not connected.</summary>
    bool Configured { get; }

    /// <summary>The open pull requests that update dependencies, opened by a bot.</summary>
    Task<IReadOnlyList<PullRequestInfo>> ListDependencyPullRequestsAsync(string repositoryFullName, CancellationToken cancellationToken);
}

/// <summary>Used when no forge is configured.</summary>
public sealed class NoPullRequestSource : IPullRequestSource
{
    public string Kind => "none";

    public bool Configured => false;

    public Task<IReadOnlyList<PullRequestInfo>> ListDependencyPullRequestsAsync(string repositoryFullName, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<PullRequestInfo>>([]);
    }
}
