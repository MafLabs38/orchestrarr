using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orchestrarr.Core.Infrastructure;

namespace Orchestrarr.Providers.GitHub;

/// <summary>
/// Reads the files of the repositories of the scope from GitHub's REST API: the tree of the default branch, then the files that a path of the description matches
/// (ADR 0028). Read-only; a file that holds a secret is never read; a file already read is not read again while its identifier is the same. A repository that cannot be
/// read is told as a problem, the others are still read.
/// </summary>
public sealed partial class GitHubInfrastructureSource : IInfrastructureSource
{
    private const int RegexTimeoutMilliseconds = 250;
    private const char UrlSeparator = '/';
    private const int MaxFilesPerRepository = 300;
    private const long MaxFileBytes = 256 * 1024;

    private readonly HttpClient _http;
    private readonly Dictionary<(string Repository, string Path), (string Sha, string Content)> _files = [];

    internal GitHubInfrastructureSource(HttpClient http) => _http = http;

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9._-]{1,100}$", RegexOptions.None, RegexTimeoutMilliseconds)]
    private static partial Regex RepositoryName();

    [GeneratedRegex(@"^[0-9a-f]{40}(?:[0-9a-f]{24})?$", RegexOptions.None, RegexTimeoutMilliseconds)]
    private static partial Regex ObjectId();

    /// <summary>Builds the source. Redirects are never followed: the token goes to the configured address only. <paramref name="handler"/> replaces the network in tests.</summary>
    public static GitHubInfrastructureSource Create(GitHubOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Token))
        {
            throw new InvalidOperationException("GitHub:Token is not set.");
        }
        var api = options.ApiUrl;
        if (!api.IsAbsoluteUri || !(api.Scheme == Uri.UriSchemeHttps || (api.Scheme == Uri.UriSchemeHttp && api.IsLoopback)))
        {
            throw new InvalidOperationException("GitHub:ApiUrl must be an https address.");
        }
        handler ??= new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(api.AbsoluteUri.TrimEnd(UrlSeparator) + UrlSeparator),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
            MaxResponseContentBufferSize = options.MaxResponseBytes,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Orchestrarr");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.Token);
        return new GitHubInfrastructureSource(http);
    }

    public async Task<InfrastructureSnapshot> ReadAsync(IReadOnlyList<RepositoryScope> scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var files = new List<InfrastructureFile>();
        var problems = new List<RepositoryProblem>();
        foreach (var repository in scope)
        {
            try
            {
                var (read, problem) = await ReadRepositoryAsync(repository, cancellationToken);
                files.AddRange(read);
                if (problem is not null)
                {
                    problems.Add(new RepositoryProblem(repository.Repository, problem));
                }
            }
            catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
            {
                problems.Add(new RepositoryProblem(repository.Repository, failure is JsonException ? "unreadable answer" : "unreachable"));
            }
        }
        return new InfrastructureSnapshot(DateTimeOffset.UtcNow, files, problems);
    }

    private async Task<(List<InfrastructureFile> Files, string? Problem)> ReadRepositoryAsync(RepositoryScope repository, CancellationToken cancellationToken)
    {
        var read = new List<InfrastructureFile>();
        if (!RepositoryName().IsMatch(repository.Repository))
        {
            return (read, "not a repository name");
        }
        using var tree = await _http.GetAsync($"repos/{repository.Repository}/git/trees/HEAD?recursive=1", cancellationToken);
        if (tree.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return (read, tree.StatusCode == HttpStatusCode.NotFound ? "not found or no access" : "refused");
        }
        tree.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await tree.Content.ReadAsStringAsync(cancellationToken));
        if (document.RootElement.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
        {
            return (read, "tree too large");
        }
        var wanted = new List<(string Path, string Sha)>();
        foreach (var entry in document.RootElement.GetProperty("tree").EnumerateArray())
        {
            var path = entry.GetProperty("path").GetString();
            if (path is null || entry.GetProperty("type").GetString() != "blob" || PathGlob.IsSecretFile(path) || !repository.Paths.Any(pattern => PathGlob.IsMatch(pattern, path)))
            {
                continue;
            }
            if (entry.TryGetProperty("size", out var size) && size.GetInt64() > MaxFileBytes)
            {
                continue;
            }
            var sha = entry.GetProperty("sha").GetString();
            if (sha is not null && ObjectId().IsMatch(sha))
            {
                wanted.Add((path, sha));
            }
        }
        if (wanted.Count > MaxFilesPerRepository)
        {
            return (read, $"more than {MaxFilesPerRepository} files match");
        }
        foreach (var (path, sha) in wanted)
        {
            if (_files.TryGetValue((repository.Repository, path), out var kept) && kept.Sha == sha)
            {
                read.Add(new InfrastructureFile(repository.Repository, path, repository.Kind, kept.Content));
                continue;
            }
            using var blob = await _http.GetAsync($"repos/{repository.Repository}/git/blobs/{sha}", cancellationToken);
            if (!blob.IsSuccessStatusCode)
            {
                continue;
            }
            using var blobDocument = JsonDocument.Parse(await blob.Content.ReadAsStringAsync(cancellationToken));
            var content = Encoding.UTF8.GetString(Convert.FromBase64String(blobDocument.RootElement.GetProperty("content").GetString()!.Replace("\n", string.Empty, StringComparison.Ordinal)));
            _files[(repository.Repository, path)] = (sha, content);
            read.Add(new InfrastructureFile(repository.Repository, path, repository.Kind, content));
        }
        return (read, null);
    }
}
