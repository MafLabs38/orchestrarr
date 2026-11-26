using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orchestrarr.Core.Forge;

namespace Orchestrarr.Providers.GitHub;

/// <summary>Reads one pull request from GitHub's REST API: its head and base commits, the checks of the head and the paths that change. Read-only, redirects never followed, the token goes to the configured address only.</summary>
public sealed partial class GitHubPullRequestFacts : IPullRequestFacts, IDefaultBranchSource, IBranchSource
{
    private const int MaxPages = 10;

    private readonly HttpClient _http;

    private readonly string[] _ignored;

    private GitHubPullRequestFacts(HttpClient http, string[] ignored)
    {
        _http = http;
        _ignored = ignored;
    }

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9._-]{1,100}$", RegexOptions.None, 250)]
    private static partial Regex RepositoryName();

    public static GitHubPullRequestFacts Create(GitHubOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Token))
        {
            throw new InvalidOperationException("GitHub:Token is not set.");
        }
        var http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = options.ApiUrl, Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("orchestrarr", "1"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return new GitHubPullRequestFacts(http, options.IgnoredChecks);
    }

    public async Task<PullRequestFacts?> ReadAsync(string repositoryFullName, int number, CancellationToken cancellationToken)
    {
        if (!RepositoryName().IsMatch(repositoryFullName) || number < 1)
        {
            return null;
        }
        try
        {
            using var pull = await GetAsync($"repos/{repositoryFullName}/pulls/{number}", cancellationToken);
            if (pull is null)
            {
                return null;
            }
            var head = pull.RootElement.GetProperty("head").GetProperty("sha").GetString();
            var baseSha = pull.RootElement.GetProperty("base").GetProperty("sha").GetString();
            if (string.IsNullOrEmpty(head) || string.IsNullOrEmpty(baseSha))
            {
                return null;
            }
            var draft = pull.RootElement.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;
            var (paths, complete) = await ChangedPathsAsync(repositoryFullName, number, cancellationToken);
            var baseRef = pull.RootElement.GetProperty("base").TryGetProperty("ref", out var br) ? br.GetString() : null;
            var defaultBranch = pull.RootElement.GetProperty("base").TryGetProperty("repo", out var repo) && repo.TryGetProperty("default_branch", out var db) ? db.GetString() : null;
            var required = baseRef is null ? [] : await RequiredChecksAsync(repositoryFullName, baseRef, cancellationToken);
            var checks = await ChecksOfAsync(repositoryFullName, head, required, cancellationToken);
            var merged = pull.RootElement.TryGetProperty("merged", out var m) && m.ValueKind == JsonValueKind.True;
            var mergeCommit = merged && pull.RootElement.TryGetProperty("merge_commit_sha", out var mc) ? mc.GetString() : null;
            var author = pull.RootElement.TryGetProperty("user", out var user) && user.TryGetProperty("login", out var login) ? login.GetString() : null;
            var closed = pull.RootElement.TryGetProperty("state", out var state) && state.GetString() == "closed";
            var mergeState = GitHubPullRequestSource.MergeOf(pull.RootElement.TryGetProperty("mergeable_state", out var ms) ? ms.GetString() : null);
            if (!merged || string.IsNullOrEmpty(mergeCommit))
            {
                // The forge works out the merge state of a pull request lazily, a while after the base moved; how many commits of the base the head lacks is known at once
                var behind = baseRef is null ? null : await BehindByAsync(repositoryFullName, baseRef, head, cancellationToken);
                return new PullRequestFacts(head, baseSha, draft, checks, paths, complete, BaseRef: baseRef, DefaultBranch: defaultBranch, Author: author, Merge: mergeState, Closed: closed, BehindBy: behind);
            }
            // Once it is merged, what runs is the commit that the merge made on the base branch: its tree and the checks of its pipeline are the facts of the later worlds
            return new PullRequestFacts(head, baseSha, draft, checks, paths, complete, Merged: true, mergeCommit, await TreeOfAsync(repositoryFullName, mergeCommit, cancellationToken), await ChecksOfAsync(repositoryFullName, mergeCommit, [], cancellationToken), baseRef, defaultBranch);
            // The checks of the commit that the merge made are those that ran on it (a push), not the checks that the rules require of a pull request, which may run on pull requests only
        }
        catch (Exception failure) when (failure is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException || failure is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<DefaultBranchHead?> ReadAsync(string repositoryFullName, CancellationToken cancellationToken)
    {
        if (!RepositoryName().IsMatch(repositoryFullName))
        {
            return null;
        }
        try
        {
            using var repository = await GetAsync($"repos/{repositoryFullName}", cancellationToken);
            if (repository is null || !repository.RootElement.TryGetProperty("default_branch", out var name) || name.GetString() is not { Length: > 0 } branch)
            {
                return null;
            }
            using var head = await GetAsync($"repos/{repositoryFullName}/branches/{Uri.EscapeDataString(branch)}", cancellationToken);
            return head is not null && head.RootElement.TryGetProperty("commit", out var commit) && commit.TryGetProperty("sha", out var sha) && sha.GetString() is { Length: > 0 } headSha
                ? new DefaultBranchHead(branch, headSha)
                : null;
        }
        catch (Exception failure) when (failure is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException || failure is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.None, 250)]
    private static partial Regex Sha();

    public async Task<string?> HeadOfAsync(string repositoryFullName, string branch, CancellationToken cancellationToken)
    {
        if (!RepositoryName().IsMatch(repositoryFullName) || !Orchestrarr.Core.Missions.AgentBranch.IsName(branch))
        {
            return null;
        }
        try
        {
            using var reference = await GetAsync($"repos/{repositoryFullName}/git/ref/heads/{branch}", cancellationToken);
            return reference is not null && reference.RootElement.TryGetProperty("object", out var target) && target.TryGetProperty("sha", out var sha) ? sha.GetString() : null;
        }
        catch (Exception failure) when (failure is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException || failure is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<BranchChange?> CompareAsync(string repositoryFullName, string baseBranch, string headSha, CancellationToken cancellationToken)
    {
        if (!RepositoryName().IsMatch(repositoryFullName) || !Sha().IsMatch(headSha) || string.IsNullOrWhiteSpace(baseBranch) || baseBranch.Length > 100)
        {
            return null;
        }
        try
        {
            using var comparison = await GetAsync($"repos/{repositoryFullName}/compare/{Uri.EscapeDataString(baseBranch)}...{headSha}?per_page=100", cancellationToken);
            if (comparison is null || !comparison.RootElement.TryGetProperty("merge_base_commit", out var mergeBase) || mergeBase.GetProperty("sha").GetString() is not { Length: > 0 } baseSha)
            {
                return null;
            }
            var root = comparison.RootElement;
            var files = root.TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(file => new BranchFile(
                    file.GetProperty("filename").GetString() ?? string.Empty,
                    file.GetProperty("status").GetString() ?? string.Empty,
                    file.TryGetProperty("changes", out var changes) && changes.TryGetInt32(out var count) ? count : 0,
                    file.TryGetProperty("patch", out var patch) ? patch.GetString() : null)).ToList()
                : [];
            // The forge lists up to 300 files, and 100 a page here: more than that is a list that was cut
            var behind = root.TryGetProperty("behind_by", out var behindBy) ? behindBy.GetInt32() : 1;
            var ahead = root.TryGetProperty("ahead_by", out var aheadBy) ? aheadBy.GetInt32() : 0;
            return new BranchChange(baseSha, behind, ahead, files, files.Count < 100);
        }
        catch (Exception failure) when (failure is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException || failure is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    // The checks that the rules of the base branch require (the rulesets of the repository, whatever sets them): the pipeline, as opposed to everything else that reports on a commit. None when the rules cannot be read or require none.
    private async Task<IReadOnlyList<string>> RequiredChecksAsync(string repository, string branch, CancellationToken cancellationToken)
    {
        using var rules = await GetAsync($"repos/{repository}/rules/branches/{Uri.EscapeDataString(branch)}", cancellationToken);
        if (rules is null || rules.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return [.. rules.RootElement.EnumerateArray()
            .Where(rule => rule.TryGetProperty("type", out var type) && type.GetString() == "required_status_checks" && rule.TryGetProperty("parameters", out _))
            .SelectMany(rule => rule.GetProperty("parameters").TryGetProperty("required_status_checks", out var list) ? list.EnumerateArray() : default)
            .Select(check => check.TryGetProperty("context", out var context) ? context.GetString() : null).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private async Task<string?> TreeOfAsync(string repository, string sha, CancellationToken cancellationToken)
    {
        using var commit = await GetAsync($"repos/{repository}/git/commits/{sha}", cancellationToken);
        return commit is not null && commit.RootElement.TryGetProperty("tree", out var tree) && tree.TryGetProperty("sha", out var treeSha) ? treeSha.GetString() : null;
    }

    private async Task<(IReadOnlyList<string> Paths, bool Complete)> ChangedPathsAsync(string repository, int number, CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var files = await GetAsync($"repos/{repository}/pulls/{number}/files?per_page=100&page={page}", cancellationToken);
            if (files is null)
            {
                return (paths, false);
            }
            var batch = files.RootElement.EnumerateArray().Select(file => file.GetProperty("filename").GetString()).OfType<string>().ToList();
            paths.AddRange(batch);
            if (batch.Count < 100)
            {
                return (paths, true);
            }
        }
        return (paths, false);
    }

    // Passing: every check run of the head is over and none failed; Pending: one is not over; Failing: one failed; Unknown: none, or not all of them could be read.
    // The runs of the forge's own dependency service (GitHub runs Dependabot's updates and its dependency graph as workflows of its own, `dynamic/dependabot/...`, on the commits of the default branch) are not
    // the project's pipeline: their failure says nothing about the commit (an image of the service that could not be pulled), and they are left out of the verdict
    private async Task<ChecksState> ChecksOfAsync(string repository, string sha, IReadOnlyList<string> required, CancellationToken cancellationToken)
    {
        var runs = new List<CheckRunRead>();
        var complete = false;
        for (var page = 1; page <= MaxPages && !complete; page++)
        {
            using var document = await GetAsync($"repos/{repository}/commits/{sha}/check-runs?per_page=100&page={page}", cancellationToken);
            if (document is null || !document.RootElement.TryGetProperty("check_runs", out var list))
            {
                return ChecksState.Unknown;
            }
            runs.AddRange(list.EnumerateArray().Select(CheckRunRead.Of));
            var total = document.RootElement.TryGetProperty("total_count", out var count) && count.TryGetInt32(out var number) ? number : runs.Count;
            if (runs.Count >= total)
            {
                complete = true;
            }
            else if (list.GetArrayLength() == 0)
            {
                return ChecksState.Unknown;
            }
        }
        // A green that was read only in part is not a green: unknown counts as failing for whoever asks
        if (!complete)
        {
            return ChecksState.Unknown;
        }
        // What is judged: the checks that the rules of the branch require, when they name some; else every check but the ones that the operator listed
        var judged = runs.Where(run => required.Count > 0 ? run.Name is not null && required.Contains(run.Name, StringComparer.OrdinalIgnoreCase) : run.Name is null || !_ignored.Contains(run.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        // The forge's own service is read only when it could change the verdict: a run that is not green
        if (judged.Any(run => !run.IsGreen) && await ServiceSuitesAsync(repository, sha, cancellationToken) is { Count: > 0 } service)
        {
            judged = [.. judged.Where(run => run.Suite is not { } suite || !service.Contains(suite))];
        }
        if (judged.Count == 0 && required.Count == 0)
        {
            return ChecksState.Unknown;
        }
        if (judged.Any(run => run.IsCompleted && !run.IsGreen))
        {
            return ChecksState.Failing;
        }
        // A required check that has not appeared yet is a pipeline that is not over
        var present = judged.Select(run => run.Name).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return judged.Any(run => !run.IsCompleted) || required.Any(context => !present.Contains(context)) ? ChecksState.Pending : ChecksState.Passing;
    }

    // The check suites of a commit that the forge's dependency service made (its workflow runs under `dynamic/dependabot/`): read once, and an answer that cannot be read leaves nothing out
    private async Task<HashSet<long>> ServiceSuitesAsync(string repository, string sha, CancellationToken cancellationToken)
    {
        var suites = new HashSet<long>();
        using var document = await GetAsync($"repos/{repository}/actions/runs?head_sha={sha}&event=dynamic&per_page=100", cancellationToken);
        if (document is null || !document.RootElement.TryGetProperty("workflow_runs", out var list))
        {
            return suites;
        }
        foreach (var run in list.EnumerateArray())
        {
            if (run.TryGetProperty("path", out var path) && path.GetString() is { } file && file.StartsWith("dynamic/dependabot/", StringComparison.Ordinal)
                && run.TryGetProperty("check_suite_id", out var suite) && suite.TryGetInt64(out var id))
            {
                _ = suites.Add(id);
            }
        }
        return suites;
    }

    // One check run as it was read: its name, whether it is over and green, and the suite it belongs to
    private sealed record CheckRunRead(string? Name, bool IsCompleted, bool IsGreen, long? Suite)
    {
        public static CheckRunRead Of(JsonElement run)
        {
            var completed = run.GetProperty("status").GetString() == "completed";
            var green = completed && run.GetProperty("conclusion").GetString() is "success" or "neutral" or "skipped";
            long? suite = run.TryGetProperty("check_suite", out var owner) && owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("id", out var id) && id.TryGetInt64(out var number) ? number : null;
            return new CheckRunRead(run.TryGetProperty("name", out var name) ? name.GetString() : null, completed, green, suite);
        }
    }

    private async Task<int?> BehindByAsync(string repositoryFullName, string baseRef, string head, CancellationToken cancellationToken)
    {
        if (!Sha().IsMatch(head) || baseRef.Length > 100 || baseRef.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '/')))
        {
            return null;
        }
        using var compared = await GetAsync($"repos/{repositoryFullName}/compare/{Uri.EscapeDataString(baseRef)}...{head}?per_page=1", cancellationToken);
        return compared is not null && compared.RootElement.TryGetProperty("behind_by", out var behind) && behind.TryGetInt32(out var count) ? count : null;
    }

    private async Task<JsonDocument?> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        return response.IsSuccessStatusCode ? await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken) : null;
    }
}
