namespace Orchestrarr.Core.Forge;

/// <summary>What the forge says about one pull request, read by the control plane itself (FR-MAN-7): the commits, the checks of the head and the paths that change. None of it comes from a worker.</summary>
public sealed record PullRequestFacts(string HeadSha, string BaseSha, bool Draft, ChecksState Checks, IReadOnlyList<string> ChangedPaths, bool PathsComplete);

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
