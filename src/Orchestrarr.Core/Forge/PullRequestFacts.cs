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
    ChecksState MergeChecks = ChecksState.Unknown);

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
}

public sealed class NoForgeActs : IForgeActs
{
    public Task<MergeResult> MergePullRequestAsync(string repositoryFullName, int number, string expectedHeadSha, CancellationToken cancellationToken) =>
        Task.FromResult(new MergeResult(false, null, "no forge is connected"));
}
