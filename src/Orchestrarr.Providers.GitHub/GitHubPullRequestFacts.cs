using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orchestrarr.Core.Forge;

namespace Orchestrarr.Providers.GitHub;

/// <summary>Reads one pull request from GitHub's REST API: its head and base commits, the checks of the head and the paths that change. Read-only, redirects never followed, the token goes to the configured address only.</summary>
public sealed partial class GitHubPullRequestFacts : IPullRequestFacts
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
            if (!merged || string.IsNullOrEmpty(mergeCommit))
            {
                return new PullRequestFacts(head, baseSha, draft, checks, paths, complete, BaseRef: baseRef, DefaultBranch: defaultBranch);
            }
            // Once it is merged, what runs is the commit that the merge made on the base branch: its tree and the checks of its pipeline are the facts of the later worlds
            return new PullRequestFacts(head, baseSha, draft, checks, paths, complete, Merged: true, mergeCommit, await TreeOfAsync(repositoryFullName, mergeCommit, cancellationToken), await ChecksOfAsync(repositoryFullName, mergeCommit, required, cancellationToken), baseRef, defaultBranch);
        }
        catch (Exception failure) when (failure is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException or InvalidOperationException)
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

    // Passing: every check run of the head is over and none failed; Pending: one is not over; Failing: one failed; Unknown: none, or not all of them could be read
    private async Task<ChecksState> ChecksOfAsync(string repository, string sha, IReadOnlyList<string> required, CancellationToken cancellationToken)
    {
        var pending = false;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = 0;
        var judged = 0;
        for (var page = 1; page <= MaxPages; page++)
        {
            using var runs = await GetAsync($"repos/{repository}/commits/{sha}/check-runs?per_page=100&page={page}", cancellationToken);
            if (runs is null || !runs.RootElement.TryGetProperty("check_runs", out var list))
            {
                return ChecksState.Unknown;
            }
            foreach (var run in list.EnumerateArray())
            {
                seen++;
                // What is judged: the checks that the rules of the branch require, when they name some; else every check but the ones that the operator listed. A run that is not judged is counted as read (the total is of every run)
                var name = run.TryGetProperty("name", out var runName) ? runName.GetString() : null;
                var isJudged = required.Count > 0 ? name is not null && required.Contains(name, StringComparer.OrdinalIgnoreCase) : name is null || !_ignored.Contains(name, StringComparer.OrdinalIgnoreCase);
                if (!isJudged)
                {
                    continue;
                }
                if (name is not null)
                {
                    present.Add(name);
                }
                judged++;
                if (run.GetProperty("status").GetString() != "completed")
                {
                    pending = true;
                }
                else if (run.GetProperty("conclusion").GetString() is not ("success" or "neutral" or "skipped"))
                {
                    return ChecksState.Failing;
                }
            }
            var total = runs.RootElement.TryGetProperty("total_count", out var count) && count.TryGetInt32(out var number) ? number : seen;
            if (seen >= total || list.GetArrayLength() == 0)
            {
                // A green that was read only in part is not a green: unknown counts as failing for whoever asks
                if (judged == 0 && required.Count == 0 || seen < total)
                {
                    return ChecksState.Unknown;
                }
                // A required check that has not appeared yet is a pipeline that is not over
                return pending || required.Any(context => !present.Contains(context)) ? ChecksState.Pending : ChecksState.Passing;
            }
        }
        return ChecksState.Unknown;
    }

    private async Task<JsonDocument?> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        return response.IsSuccessStatusCode ? await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken) : null;
    }
}
