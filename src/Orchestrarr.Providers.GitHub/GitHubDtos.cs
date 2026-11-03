namespace Orchestrarr.Providers.GitHub;

// The parts of GitHub's REST answers that Orchestrarr reads. The links of the answers are ignored on purpose.

internal sealed record GitHubUser(string? Login = null);

internal sealed record GitHubRef(string? Ref = null, string? Sha = null);

internal sealed record GitHubLabel(string? Name = null);

internal sealed record GitHubPullRequest(
    int Number,
    string? Title = null,
    GitHubUser? User = null,
    GitHubRef? Head = null,
    bool Draft = false,
    IReadOnlyList<GitHubLabel>? Labels = null,
    DateTimeOffset? CreatedAt = null,
    string? MergeableState = null);

internal sealed record GitHubCheckRun(string? Status = null, string? Conclusion = null);

internal sealed record GitHubCheckRuns(IReadOnlyList<GitHubCheckRun>? CheckRuns = null);

internal sealed record GitHubCombinedStatus(string? State = null, int TotalCount = 0);
