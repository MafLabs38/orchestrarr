namespace Orchestrarr.Core.Forge;

/// <summary>
/// What the forge says about one pull request, read by the control plane itself (FR-MAN-7): the commits, the checks of the head and the paths that change; once it is merged, the commit that the merge made on the base branch, its tree
/// and the checks of that commit. None of it comes from a worker. <c>HeadPushedBy</c> is the account that pushed the head to its branch, as the forge recorded the push (null when it cannot be read): unlike the author of a commit, it
/// cannot be written by whoever made the commit.
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
    string? DefaultBranch = null,
    string? Author = null,
    MergeState Merge = MergeState.Unknown,
    bool Closed = false,
    int? BehindBy = null,
    IReadOnlyList<string>? CommitAuthors = null,
    string? HeadPushedBy = null,
    bool? BaseWasRed = null)
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

/// <summary>What the forge answered to a merge: done, with the commit it made on the base branch, or why not. <c>ChecksPending</c>: the forge refused only because a check that its rules require has not reported yet, a wait and not a refusal (each provider knows how its forge says it).</summary>
public sealed record MergeResult(bool Merged, string? Commit, string? Error, bool ChecksPending = false);

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

    /// <summary>
    /// Opens a pull request from a branch that already exists on the forge (the branch of a worker agent, ADR 0048), **at the commit that was audited**: nothing is opened if the branch is no longer at <paramref name="expectedSha"/>.
    /// The branch and its commits are the agent's own; the gateway writes only the pull request.
    /// </summary>
    Task<OpenedPullRequest> OpenPullRequestFromBranchAsync(string repositoryFullName, string baseBranch, string branch, string expectedSha, string title, string body, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the bot that made a pull request to rebase it (a comment with the bot's own command, ADR 0055): nothing is merged, no branch is written, and the bot is the one that changes its branch. False when the forge did not take the comment.
    /// </summary>
    Task<bool> RequestBotRebaseAsync(string repositoryFullName, int number, string command, CancellationToken cancellationToken);
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

    public Task<OpenedPullRequest> OpenPullRequestFromBranchAsync(string repositoryFullName, string baseBranch, string branch, string expectedSha, string title, string body, CancellationToken cancellationToken) =>
        Task.FromResult(new OpenedPullRequest(false, 0, null, "no forge is connected"));

    public Task<bool> RequestBotRebaseAsync(string repositoryFullName, int number, string command, CancellationToken cancellationToken) => Task.FromResult(false);
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

/// <summary>
/// The pipelines that are red on the default branch of a repository, read from the forge: for each workflow of the repository, its last run on that branch, when it did not pass (a red pipeline is a problem whose
/// worker must know **which** pipeline to repair, ADR 0055). One plain line each (the file of the workflow, its name, how it ended). The forge's own jobs are not the repository's pipelines. Empty when nothing can be read.
/// </summary>
public interface IPipelineFailures
{
    Task<IReadOnlyList<string>> ReadAsync(string repositoryFullName, CancellationToken cancellationToken);
}

/// <summary>Used when no forge is connected.</summary>
public sealed class NoPipelineFailures : IPipelineFailures
{
    public Task<IReadOnlyList<string>> ReadAsync(string repositoryFullName, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>One file of what a branch changes, as the forge says it (never as a runner says it): the path, what happened to it, how many lines, and the patch when the forge gives one.</summary>
public sealed record BranchFile(string Path, string Status, int Changes, string? Patch);

/// <summary>
/// What a branch holds on top of the default branch, read from the forge: the common commit, how far the default branch has gone since, and the files. <c>FilesComplete</c> is false when the forge cut the list.
/// </summary>
public sealed record BranchChange(string MergeBase, int BehindBy, int AheadBy, IReadOnlyList<BranchFile> Files, bool FilesComplete);

/// <summary>Reads a worker agent's branch from the forge (ADR 0048): where it is, and what it changes against the default branch. Null when it cannot be read: nothing is judged on what was not read.</summary>
public interface IBranchSource
{
    /// <summary>The commit that the branch points at now, or null when there is no such branch.</summary>
    Task<string?> HeadOfAsync(string repositoryFullName, string branch, CancellationToken cancellationToken);

    /// <summary>The change that <paramref name="headSha"/> makes on top of <paramref name="baseBranch"/>.</summary>
    Task<BranchChange?> CompareAsync(string repositoryFullName, string baseBranch, string headSha, CancellationToken cancellationToken);

    /// <summary>A file at a commit (<see cref="FileAt"/>), or null when it cannot be read: a repair is then judged on the whole file it carries, and what it removes from a pipeline cannot be judged (ADR 0055).</summary>
    Task<FileAt?> FileAtAsync(string repositoryFullName, string sha, string path, CancellationToken cancellationToken) => Task.FromResult<FileAt?>(null);

    /// <summary>The paths of every file at a commit, or null when they cannot all be listed: a change of a pipeline's path filters is measured on them (ADR 0057).</summary>
    Task<IReadOnlyList<string>?> FilesAtAsync(string repositoryFullName, string sha, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>?>(null);
}

/// <summary>A file read at a commit: its text, or that the commit has no such file.</summary>
public sealed record FileAt(bool Exists, string? Text);

/// <summary>Used when no forge is connected.</summary>
public sealed class NoBranchSource : IBranchSource
{
    public Task<string?> HeadOfAsync(string repositoryFullName, string branch, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<BranchChange?> CompareAsync(string repositoryFullName, string baseBranch, string headSha, CancellationToken cancellationToken) => Task.FromResult<BranchChange?>(null);
}
