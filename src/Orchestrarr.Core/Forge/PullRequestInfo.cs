using System.Net;

namespace Orchestrarr.Core.Forge;

/// <summary>Whether the checks of a pull request pass. Unknown: none ran, or none could be read.</summary>
public enum ChecksState
{
    Unknown,
    Pending,
    Passing,
    Failing,

    /// <summary>The checks that fail are only the ones that cannot run for a bot's pull request (they need secrets that the forge does not give to a bot): it says nothing about the code.</summary>
    NotRun,
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

    /// <summary>A pinned base image moved to another digest: a rebuild of the same version.</summary>
    Digest,

    /// <summary>A pull request that updates a group of packages: the versions are not read.</summary>
    Group,
}

/// <summary>One dependency a pull request updates. Versions are known only for a pull request that updates a single one.</summary>
public sealed record DependencyChange(string Package, string? From, string? To, UpdateKind Kind);

/// <summary>
/// An open pull request of a dependency bot. <c>HeadSha</c> is the commit that was read: an approval binds to
/// (repository, number, head commit), so that a bot that rewrites its branch after the operator ticked is not merged unseen.
/// <c>ReadyToMerge</c> is decided here, once, for every client.
/// </summary>
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
    DateTimeOffset CreatedAt,
    string HeadSha = "")
{
    /// <summary>Not a draft, every check passes and the forge says it can be merged as it stands.</summary>
    public bool ReadyToMerge => !Draft && Checks == ChecksState.Passing && Merge == MergeState.Clean;
}

/// <summary>What the forge returned for a repository. <c>Truncated</c>: there are more bot pull requests than were read.</summary>
public sealed record PullRequestList(IReadOnlyList<PullRequestInfo> Items, bool Truncated);

/// <summary>A forge (GitHub first): where the pull requests of a repository are read. Read-only in this vertical.</summary>
public interface IPullRequestSource
{
    string Kind { get; }

    /// <summary>False for the source that reads nothing: the screens say the forge is not connected.</summary>
    bool Configured { get; }

    /// <summary>The open pull requests that update dependencies, opened by a bot. <paramref name="refresh"/> bypasses what was kept.</summary>
    Task<PullRequestList> ListDependencyPullRequestsAsync(string repositoryFullName, bool refresh, CancellationToken cancellationToken);
}

/// <summary>Used when no forge is configured.</summary>
public sealed class NoPullRequestSource : IPullRequestSource
{
    public string Kind => "none";

    public bool Configured => false;

    public Task<PullRequestList> ListDependencyPullRequestsAsync(string repositoryFullName, bool refresh, CancellationToken cancellationToken)
    {
        return Task.FromResult(new PullRequestList([], Truncated: false));
    }
}

/// <summary>The forge refuses to answer more for a while. Its own error, because it is not a refusal of the credentials.</summary>
public sealed class ForgeRateLimitedException : HttpRequestException
{
    public ForgeRateLimitedException(HttpStatusCode statusCode, TimeSpan? retryAfter)
        : base("The forge rate limit is reached.", null, statusCode)
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan? RetryAfter { get; }
}
