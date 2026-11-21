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

    private GitHubPullRequestFacts(HttpClient http) => _http = http;

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
        return new GitHubPullRequestFacts(http);
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
            return new PullRequestFacts(head, baseSha, draft, await ChecksOfAsync(repositoryFullName, head, cancellationToken), paths, complete);
        }
        catch (Exception failure) when (failure is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
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
    private async Task<ChecksState> ChecksOfAsync(string repository, string sha, CancellationToken cancellationToken)
    {
        var pending = false;
        var seen = 0;
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
                if (seen == 0 || seen < total)
                {
                    return ChecksState.Unknown;
                }
                return pending ? ChecksState.Pending : ChecksState.Passing;
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
