using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orchestrarr.Core.Forge;

namespace Orchestrarr.Providers.GitHub;

/// <summary>Reads the dependency pull requests of a repository from GitHub's REST API. Read-only: it never writes to GitHub.</summary>
public sealed partial class GitHubPullRequestSource : IPullRequestSource
{
    private const char UrlSeparator = '/';

    private const int RegexTimeoutMilliseconds = 250;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;
    private readonly GitHubOptions _options;
    private readonly Uri _webUrl;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, (DateTimeOffset At, PullRequestList List)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    internal GitHubPullRequestSource(HttpClient http, GitHubOptions options, Uri webUrl, TimeProvider time)
    {
        _http = http;
        _options = options;
        _webUrl = webUrl;
        _time = time;
    }

    public string Kind => "github";

    public bool Configured => true;

    // owner/name: what GitHub allows (an owner neither starts nor ends with a hyphen), nothing that could change a route (a name made of dots is refused below)
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9._-]{1,100}$", RegexOptions.None, RegexTimeoutMilliseconds)]
    private static partial Regex RepositoryName();

    // A commit id, as it goes into a path: 40 hexadecimal characters (64 with SHA-256)
    [GeneratedRegex(@"^[0-9a-f]{40}(?:[0-9a-f]{24})?$", RegexOptions.None, RegexTimeoutMilliseconds)]
    private static partial Regex CommitId();

    [GeneratedRegex(@"<(?<url>[^>]+)>\s*;\s*rel=""next""", RegexOptions.None, RegexTimeoutMilliseconds)]
    private static partial Regex NextLink();

    /// <summary>
    /// Builds the source. Redirects are never followed: the token must go to the configured address only. <paramref name="handler"/>
    /// replaces the network in tests. A setting that cannot be used stops the start, naming the key and never the value.
    /// </summary>
    public static GitHubPullRequestSource Create(GitHubOptions options, TimeProvider time, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Token))
        {
            throw new InvalidOperationException("GitHub:Token is not set.");
        }

        var apiUrl = Normalize(options.ApiUrl, "GitHub:ApiUrl", allowLoopbackHttp: true);
        var webUrl = Normalize(options.WebUrl, "GitHub:WebUrl", allowLoopbackHttp: false);
        Positive(options.MaxPullRequests, "GitHub:MaxPullRequests");
        Positive(options.MaxPages, "GitHub:MaxPages");
        Positive(options.TimeoutSeconds, "GitHub:TimeoutSeconds");
        Positive(options.MaxResponseBytes, "GitHub:MaxResponseBytes");
        Positive(options.CacheSeconds + 1, "GitHub:CacheSeconds");

        handler ??= new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        var http = new HttpClient(handler)
        {
            BaseAddress = apiUrl,
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
            MaxResponseContentBufferSize = options.MaxResponseBytes,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Orchestrarr");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.Token);
        return new GitHubPullRequestSource(http, options, webUrl, time);
    }

    public async Task<PullRequestList> ListDependencyPullRequestsAsync(string repositoryFullName, bool refresh, CancellationToken cancellationToken)
    {
        ValidateRepository(repositoryFullName);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            if (!refresh && _cache.TryGetValue(repositoryFullName, out var cached) && now - cached.At < TimeSpan.FromSeconds(_options.CacheSeconds))
            {
                return cached.List;
            }

            var list = await ReadAsync(repositoryFullName, cancellationToken);
            _cache[repositoryFullName] = (now, list);
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static Uri Normalize(Uri url, string key, bool allowLoopbackHttp)
    {
        var secure = url.Scheme == Uri.UriSchemeHttps || (allowLoopbackHttp && url.Scheme == Uri.UriSchemeHttp && url.IsLoopback);
        if (!url.IsAbsoluteUri || !secure)
        {
            throw new InvalidOperationException($"{key} must be an https address.");
        }

        // A base address always ends with one separator, so that a relative path is added to it, not in place of its last segment
        return new Uri(url.AbsoluteUri.TrimEnd(UrlSeparator) + UrlSeparator);
    }

    private static void Positive(long value, string key)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException($"{key} must be greater than zero.");
        }
    }

    private static void ValidateRepository(string fullName)
    {
        var slash = fullName.IndexOf('/', StringComparison.Ordinal);
        var name = slash < 0 ? fullName : fullName[(slash + 1)..];
        if (!RepositoryName().IsMatch(fullName) || name is "." or "..")
        {
            throw new ArgumentException("Not a repository name (owner/name).", nameof(fullName));
        }
    }

    private async Task<PullRequestList> ReadAsync(string repository, CancellationToken cancellationToken)
    {
        var listed = new List<GitHubPullRequest>();
        var next = $"repos/{repository}/pulls?state=open&per_page=100";
        var morePages = false;
        var page = 0;
        while (next is not null)
        {
            page++;
            var (body, link) = await GetAsync<List<GitHubPullRequest>>(next, cancellationToken);
            // Offset pagination can give a pull request twice when the list shifts between two pages: one copy is kept
            listed.AddRange((body ?? []).Where(pullRequest => listed.TrueForAll(known => known.Number != pullRequest.Number)));
            next = link;
            if (next is not null && page >= _options.MaxPages)
            {
                morePages = true;
                break;
            }
        }

        var bots = listed
            .Where(pr => pr.User?.Login is { } login && _options.Bots.Contains(login, StringComparer.OrdinalIgnoreCase))
            .OrderBy(pr => pr.Number)
            .ToList();

        var result = new List<PullRequestInfo>(Math.Min(bots.Count, _options.MaxPullRequests));
        foreach (var pr in bots.Take(_options.MaxPullRequests))
        {
            result.Add(await ReadOneAsync(repository, pr, cancellationToken));
        }

        return new PullRequestList(result, Truncated: morePages || bots.Count > _options.MaxPullRequests);
    }

    // One pull request that cannot be read (closed meanwhile, a hiccup of the forge) is shown with what is unknown about it,
    // it does not take the rest of the repository down. A refusal or a rate limit is about everything, and goes up.
    private async Task<PullRequestInfo> ReadOneAsync(string repository, GitHubPullRequest listed, CancellationToken cancellationToken)
    {
        var sha = listed.Head?.Sha is { } head && CommitId().IsMatch(head) ? head : string.Empty;
        try
        {
            var (detail, _) = await GetAsync<GitHubPullRequest>($"repos/{repository}/pulls/{listed.Number}", cancellationToken);
            var checks = sha.Length > 0 ? await ChecksAsync(repository, sha, cancellationToken) : ChecksState.Unknown;
            return Map(repository, listed, detail ?? listed, checks, sha);
        }
        catch (HttpRequestException ex) when (ex is not ForgeRateLimitedException && (ex.StatusCode == HttpStatusCode.NotFound || ex.StatusCode >= HttpStatusCode.InternalServerError))
        {
            return Map(repository, listed, listed, ChecksState.Unknown, sha);
        }
    }

    private async Task<ChecksState> ChecksAsync(string repository, string sha, CancellationToken cancellationToken)
    {
        var (runs, _) = await GetAsync<GitHubCheckRuns>($"repos/{repository}/commits/{sha}/check-runs?per_page=100", cancellationToken);
        if (runs?.CheckRuns is { Count: > 0 } checkRuns)
        {
            return ChecksOf(checkRuns, _options.NeedSecrets);
        }

        // No check run: older CI systems report a commit status instead. A token without that permission reads nothing here, which is unknown.
        try
        {
            var (status, _) = await GetAsync<GitHubCombinedStatus>($"repos/{repository}/commits/{sha}/status", cancellationToken);
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
        catch (HttpRequestException ex) when (ex is not ForgeRateLimitedException && ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return ChecksState.Unknown;
        }
    }

    internal static ChecksState ChecksOf(IReadOnlyList<GitHubCheckRun> runs, IReadOnlyList<string>? needSecrets = null)
    {
        static bool Failed(GitHubCheckRun run) => run.Conclusion?.ToLowerInvariant() is "failure" or "cancelled" or "timed_out" or "action_required" or "startup_failure";

        var failed = runs.Where(Failed).ToList();
        if (failed.Count > 0)
        {
            // These are pull requests of bots: a check that needs a secret cannot run for them, and its failure says nothing about the code
            if (needSecrets is { Count: > 0 } && failed.All(run => NeedsSecret(run.Name, needSecrets)))
            {
                // The others may still fail for a real reason: while one runs, it is running
                return runs.Any(run => !string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase)) ? ChecksState.Pending : ChecksState.NotRun;
            }
            return ChecksState.Failing;
        }

        return runs.Any(run => !string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase)) ? ChecksState.Pending : ChecksState.Passing;
    }

    private PullRequestInfo Map(string repository, GitHubPullRequest listed, GitHubPullRequest detail, ChecksState checks, string sha)
    {
        var title = listed.Title ?? string.Empty;
        return new PullRequestInfo(
            Number: listed.Number,
            Title: title,
            Url: new Uri(_webUrl, $"{repository}/pull/{listed.Number}"),
            Author: listed.User?.Login ?? string.Empty,
            HeadBranch: listed.Head?.Ref ?? string.Empty,
            Draft: listed.Draft,
            Checks: checks,
            Merge: MergeOf(detail.MergeableState),
            Labels: [.. (listed.Labels ?? []).Select(l => l.Name).OfType<string>()],
            Changes: DependencyTitle.Parse(title),
            CreatedAt: listed.CreatedAt ?? default,
            HeadSha: sha);
    }

    private static bool NeedsSecret(string? name, IReadOnlyList<string> patterns) =>
        name is not null && patterns.Any(pattern => pattern.EndsWith('*') ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase) : string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase));

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

    // The next page is only followed when it is on the configured API: the token goes nowhere else
    private string? NextPath(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        var match = values.Select(v => NextLink().Match(v)).FirstOrDefault(m => m.Success);
        if (match is null || !Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out var url))
        {
            return null;
        }

        var basePath = _http.BaseAddress!.AbsolutePath;
        var sameApi = url.Scheme == _http.BaseAddress.Scheme && url.Authority == _http.BaseAddress.Authority && url.AbsolutePath.StartsWith(basePath, StringComparison.Ordinal);
        return sameApi ? url.PathAndQuery[basePath.Length..] : null;
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        return header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
    }

    // GitHub answers a rate limit with 429, or with 403 and no request left: that is not a refusal of the credentials
    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        return response.StatusCode == HttpStatusCode.Forbidden
            && (response.Headers.RetryAfter is not null || (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0")));
    }

    private async Task<(T? Body, string? Next)> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken);
        if (IsRateLimited(response))
        {
            throw new ForgeRateLimitedException(response.StatusCode, RetryAfter(response));
        }

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken), NextPath(response));
    }
}
