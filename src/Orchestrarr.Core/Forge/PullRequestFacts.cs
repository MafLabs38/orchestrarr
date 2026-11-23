namespace Orchestrarr.Core.Forge;

/// <summary>
/// What the forge says about one pull request, read by the control plane itself (FR-MAN-7): the commits, the checks of the head and the paths that change; once it is merged, the commit that the merge made on the base branch, its tree
/// and the checks of that commit. None of it comes from a worker.
/// </summary>
public sealed record PullRequestFacts(
    string HeadSha,
    string BaseSha,
    bool Draft,
    ChecksState Checks,
    IReadOnlyList<string> ChangedPaths,
    bool PathsComplete,
    bool Merged = false,
    string? MergeCommit = null,
    string? MergeTree = null,
    ChecksState MergeChecks = ChecksState.Unknown,
    string? BaseRef = null,
    string? DefaultBranch = null)
{
    /// <summary>Whether the pull request targets the default branch of its repository: the only base that staging proves a merge onto. Unknown counts as not.</summary>
    public bool TargetsDefaultBranch => BaseRef is not null && string.Equals(BaseRef, DefaultBranch, StringComparison.Ordinal);
}

/// <summary>The reader of those facts. Null: the pull request cannot be read now (unknown counts as failing for whoever asks).</summary>
public interface IPullRequestFacts
{
    Task<PullRequestFacts?> ReadAsync(string repositoryFullName, int number, CancellationToken cancellationToken);
}

/// <summary>Used when no forge is connected.</summary>
public sealed class NoPullRequestFacts : IPullRequestFacts
{
    public Task<PullRequestFacts?> ReadAsync(string repositoryFullName, int number, CancellationToken cancellationToken) => Task.FromResult<PullRequestFacts?>(null);
}

/// <summary>What the forge answered to a merge: done, with the commit it made on the base branch, or why not.</summary>
public sealed record MergeResult(bool Merged, string? Commit, string? Error);

/// <summary>
/// **The act of merging**, performed by the control plane's gateway and by nobody else, after the conductor authorised it on this exact head. The forge is told the head that was authorised and refuses if the branch moved since
/// (the merge call takes the expected head). Where no forge is connected nothing is merged.
/// </summary>
public interface IForgeActs
{
    Task<MergeResult> MergePullRequestAsync(string repositoryFullName, int number, string expectedHeadSha, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a pull request that carries a repair: a branch of the given name is made from <paramref name="parentSha"/> with the files written whole into one commit, and a pull request is opened from it on <paramref name="baseBranch"/>. The branch is
    /// the only thing written; nothing existing is changed.
    /// </summary>
    Task<OpenedPullRequest> OpenPullRequestAsync(string repositoryFullName, string baseBranch, string parentSha, string branch, IReadOnlyList<Orchestrarr.Core.Missions.PatchedFile> files, string title, string body, CancellationToken cancellationToken);

    /// <summary>Closes a pull request that another one replaces, saying so. False when the forge did not do it (it is said, not hidden).</summary>
    Task<bool> ClosePullRequestAsync(string repositoryFullName, int number, string comment, CancellationToken cancellationToken);
}

/// <summary>What the forge answered to the opening of a pull request: its number and the head of its branch, or why not.</summary>
public sealed record OpenedPullRequest(bool Opened, int Number, string? HeadSha, string? Error);

public sealed class NoForgeActs : IForgeActs
{
    public Task<MergeResult> MergePullRequestAsync(string repositoryFullName, int number, string expectedHeadSha, CancellationToken cancellationToken) =>
        Task.FromResult(new MergeResult(false, null, "no forge is connected"));

    public Task<OpenedPullRequest> OpenPullRequestAsync(string repositoryFullName, string baseBranch, string parentSha, string branch, IReadOnlyList<Orchestrarr.Core.Missions.PatchedFile> files, string title, string body, CancellationToken cancellationToken) =>
        Task.FromResult(new OpenedPullRequest(false, 0, null, "no forge is connected"));

    public Task<bool> ClosePullRequestAsync(string repositoryFullName, int number, string comment, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>The default branch of a repository and its head now: what a change that has no pull request is made against (ADR 0048).</summary>
public sealed record DefaultBranchHead(string Branch, string HeadSha);

public interface IDefaultBranchSource
{
    /// <summary>Null when the repository or its branch cannot be read now: nothing is written on a head that was not read.</summary>
    Task<DefaultBranchHead?> ReadAsync(string repositoryFullName, CancellationToken cancellationToken);
}

/// <summary>Used when no forge is connected.</summary>
public sealed class NoDefaultBranchSource : IDefaultBranchSource
{
    public Task<DefaultBranchHead?> ReadAsync(string repositoryFullName, CancellationToken cancellationToken) => Task.FromResult<DefaultBranchHead?>(null);
}
