using System.Net.Http.Json;
using System.Text.Json;
using Orchestrarr.Core.Forge;

namespace Orchestrarr.Providers.GitHub;

/// <summary>Reads the dependency pull requests of a repository from GitHub's REST API. Read-only: it never writes to GitHub.</summary>
public sealed class GitHubPullRequestSource : IPullRequestSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;
    private readonly GitHubOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<PullRequestInfo> PullRequests)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    internal GitHubPullRequestSource(HttpClient http, GitHubOptions options, TimeProvider time)
    {
        _http = http;
        _options = options;
        _time = time;
    }

    public string Kind => "github";

    public bool Configured => true;

    /// <summary>
    /// Builds the source. Redirects are never followed: the token must go to the configured address only. <paramref name="handler"/>
    /// replaces the network in tests.
    /// </summary>
    public static GitHubPullRequestSource Create(GitHubOptions options, TimeProvider time, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Token))
        {
            throw new InvalidOperationException("GitHub:Token is not set.");
        }

        handler ??= new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        var http = new HttpClient(handler)
        {
            BaseAddress = options.ApiUrl,
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
            MaxResponseContentBufferSize = options.MaxResponseBytes,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Orchestrarr");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.Token);
        return new GitHubPullRequestSource(http, options, time);
    }

    public async Task<IReadOnlyList<PullRequestInfo>> ListDependencyPullRequestsAsync(string repositoryFullName, CancellationToken cancellationToken)
    {
        ValidateRepository(repositoryFullName);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            if (_cache.TryGetValue(repositoryFullName, out var cached) && now - cached.At < TimeSpan.FromSeconds(_options.CacheSeconds))
            {
                return cached.PullRequests;
            }

            var pullRequests = await ReadAsync(repositoryFullName, cancellationToken);
            _cache[repositoryFullName] = (now, pullRequests);
            return pullRequests;
        }
        finally
        {
            _lock.Release();
        }
    }

    // The name goes into a path: only what a repository name can be, nothing that would change the route
    private static void ValidateRepository(string fullName)
    {
        var parts = fullName.Split('/');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            throw new ArgumentException("Not a repository name (owner/name).", nameof(fullName));
        }
    }

    private async Task<IReadOnlyList<PullRequestInfo>> ReadAsync(string repository, CancellationToken cancellationToken)
    {
        var listed = await GetAsync<List<GitHubPullRequest>>($"repos/{repository}/pulls?state=open&per_page=100", cancellationToken) ?? [];
        var bots = listed
            .Where(pr => pr.User?.Login is { } login && _options.BotLogins.Contains(login, StringComparer.OrdinalIgnoreCase))
            .OrderBy(pr => pr.Number)
            .Take(_options.MaxPullRequests)
            .ToList();

        var result = new List<PullRequestInfo>(bots.Count);
        foreach (var pr in bots)
        {
            // The list does not say whether a pull request can be merged: the detail does
            var detail = await GetAsync<GitHubPullRequest>($"repos/{repository}/pulls/{pr.Number}", cancellationToken) ?? pr;
            var checks = pr.Head?.Sha is { Length: > 0 } sha ? await ChecksAsync(repository, sha, cancellationToken) : ChecksState.Unknown;
            result.Add(Map(repository, pr, detail, checks));
        }

        return result;
    }

    private async Task<ChecksState> ChecksAsync(string repository, string sha, CancellationToken cancellationToken)
    {
        var runs = (await GetAsync<GitHubCheckRuns>($"repos/{repository}/commits/{sha}/check-runs?per_page=100", cancellationToken))?.CheckRuns ?? [];
        if (runs.Count > 0)
        {
            return ChecksOf(runs);
        }

        // No check run: older CI systems report a commit status instead
        var status = await GetAsync<GitHubCombinedStatus>($"repos/{repository}/commits/{sha}/status", cancellationToken);
        return status is null or { TotalCount: 0 }
            ? ChecksState.Unknown
            : status.State?.ToLowerInvariant() switch
            {
                "success" => ChecksState.Passing,
                "failure" or "error" => ChecksState.Failing,
                "pending" => ChecksState.Pending,
                _ => ChecksState.Unknown,
            };
    }

    internal static ChecksState ChecksOf(IReadOnlyList<GitHubCheckRun> runs)
    {
        static bool Failed(GitHubCheckRun run) => run.Conclusion?.ToLowerInvariant() is "failure" or "cancelled" or "timed_out" or "action_required" or "startup_failure";

        if (runs.Any(Failed))
        {
            return ChecksState.Failing;
        }

        return runs.Any(run => !string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase)) ? ChecksState.Pending : ChecksState.Passing;
    }

    private PullRequestInfo Map(string repository, GitHubPullRequest listed, GitHubPullRequest detail, ChecksState checks)
    {
        var title = listed.Title ?? string.Empty;
        return new PullRequestInfo(
            Number: listed.Number,
            Title: title,
            Url: new Uri(_options.WebUrl, $"{repository}/pull/{listed.Number}"),
            Author: listed.User?.Login ?? string.Empty,
            HeadBranch: listed.Head?.Ref ?? string.Empty,
            Draft: listed.Draft,
            Checks: checks,
            Merge: MergeOf(detail.MergeableState),
            Labels: [.. (listed.Labels ?? []).Select(l => l.Name).OfType<string>()],
            Changes: DependencyTitle.Parse(title),
            CreatedAt: listed.CreatedAt ?? default);
    }

    internal static MergeState MergeOf(string? state)
    {
        return state?.ToLowerInvariant() switch
        {
            "clean" => MergeState.Clean,
            "unstable" => MergeState.Unstable,
            "behind" => MergeState.Behind,
            "blocked" => MergeState.Blocked,
            "dirty" => MergeState.Conflicting,
            "draft" => MergeState.Draft,
            _ => MergeState.Unknown,
        };
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
    }
}
