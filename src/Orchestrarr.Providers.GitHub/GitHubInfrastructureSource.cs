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

    private readonly TimeProvider _time;

    internal GitHubInfrastructureSource(HttpClient http, TimeProvider time)
    {
        _http = http;
        _time = time;
    }

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9._-]{1,100}$", RegexOptions.None, RegexTimeoutMilliseconds)]
    private static partial Regex RepositoryName();

    [GeneratedRegex(@"^[0-9a-f]{40}(?:[0-9a-f]{24})?$", RegexOptions.None, RegexTimeoutMilliseconds)]
    private static partial Regex ObjectId();

    /// <summary>Builds the source. Redirects are never followed: the token goes to the configured address only. <paramref name="handler"/> replaces the network in tests.</summary>
    public static GitHubInfrastructureSource Create(GitHubOptions options, HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Token))
        {
            throw new InvalidOperationException("GitHub:ContentsToken (or GitHub:Token) is not set.");
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
        return new GitHubInfrastructureSource(http, time ?? TimeProvider.System);
    }

    private sealed record TreeEntry(string Path, string? Sha, long Size);

    public async Task<InfrastructureSnapshot> ReadAsync(IReadOnlyList<RepositoryScope> scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var files = new List<InfrastructureFile>();
        var problems = new List<RepositoryProblem>();
        var seen = new HashSet<(string Repository, string Path)>();
        // A repository may be named more than once (an overlay for each environment): its tree is read once
        foreach (var group in scope.GroupBy(repository => repository.Repository, StringComparer.Ordinal))
        {
            var messages = new List<string>();
            try
            {
                var (tree, problem) = await ReadTreeAsync(group.Key, cancellationToken);
                if (problem is not null)
                {
                    messages.Add(problem);
                }
                if (tree is not null)
                {
                    foreach (var repository in group)
                    {
                        var (read, unreadable) = await ReadFilesAsync(repository, tree, cancellationToken);
                        files.AddRange(read);
                        seen.UnionWith(read.Select(file => (file.Repository, file.Path)));
                        if (unreadable is not null)
                        {
                            messages.Add(unreadable);
                        }
                        else if (read.Count == 0)
                        {
                            // A path of the description that matches no file is a typo, or a repository that moved: it is told, not left as "nothing to find"
                            messages.Add("no file matches its paths");
                        }
                    }
                }
            }
            catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                messages.Add("unreachable");
            }
            catch (Exception failure) when (failure is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // An answer that is not what the API documents: the repository is told, the others are still read
                messages.Add("unreadable answer");
            }
            problems.AddRange(messages.Distinct(StringComparer.Ordinal).Select(message => new RepositoryProblem(group.Key, message)));
        }
        // What is no longer there (a deleted or renamed file, a repository taken out of the description) is not kept
        foreach (var key in _files.Keys.Where(key => !seen.Contains(key)).ToList())
        {
            _files.Remove(key);
        }
        return new InfrastructureSnapshot(_time.GetUtcNow(), files, problems);
    }

    private async Task<(List<TreeEntry>? Tree, string? Problem)> ReadTreeAsync(string repository, CancellationToken cancellationToken)
    {
        if (!RepositoryName().IsMatch(repository) || repository.EndsWith("/.", StringComparison.Ordinal) || repository.EndsWith("/..", StringComparison.Ordinal))
        {
            return (null, "not a repository name");
        }
        using var tree = await _http.GetAsync($"repos/{repository}/git/trees/HEAD?recursive=1", cancellationToken);
        if (tree.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return (null, tree.StatusCode == HttpStatusCode.NotFound ? "not found or no access" : "refused");
        }
        tree.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await tree.Content.ReadAsStringAsync(cancellationToken));
        if (document.RootElement.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
        {
            return (null, "tree too large");
        }
        var entries = new List<TreeEntry>();
        foreach (var entry in document.RootElement.GetProperty("tree").EnumerateArray())
        {
            if (entry.GetProperty("path").GetString() is { } path && entry.GetProperty("type").GetString() == "blob")
            {
                entries.Add(new TreeEntry(path, entry.GetProperty("sha").GetString(), entry.TryGetProperty("size", out var size) ? size.GetInt64() : 0));
            }
        }
        return (entries, null);
    }

    private async Task<(List<InfrastructureFile> Files, string? Problem)> ReadFilesAsync(RepositoryScope repository, List<TreeEntry> tree, CancellationToken cancellationToken)
    {
        var read = new List<InfrastructureFile>();
        var wanted = new List<(string Path, string Sha)>();
        var skipped = 0;
        foreach (var entry in tree.Where(entry => !PathGlob.IsSecretFile(entry.Path) && repository.Paths.Any(pattern => PathGlob.IsMatch(pattern, entry.Path))))
        {
            // A file that is too big, or whose identifier is not one, is not read and is told, as the files that cannot be read are
            if (entry.Size > MaxFileBytes || entry.Sha is null || !ObjectId().IsMatch(entry.Sha))
            {
                skipped++;
                continue;
            }
            wanted.Add((entry.Path, entry.Sha));
        }
        if (wanted.Count > MaxFilesPerRepository)
        {
            return (read, $"more than {MaxFilesPerRepository} files match");
        }
        var unreadable = 0;
        foreach (var (path, sha) in wanted)
        {
            if (_files.TryGetValue((repository.Repository, path), out var kept) && kept.Sha == sha)
            {
                read.Add(new InfrastructureFile(repository.Repository, path, repository.Kind, kept.Content, repository.Environment));
                continue;
            }
            using var blob = await _http.GetAsync($"repos/{repository.Repository}/git/blobs/{sha}", cancellationToken);
            if (!blob.IsSuccessStatusCode)
            {
                // A file that cannot be read is told: an image would otherwise look as if it were written nowhere
                unreadable++;
                continue;
            }
            using var blobDocument = JsonDocument.Parse(await blob.Content.ReadAsStringAsync(cancellationToken));
            var encoded = blobDocument.RootElement.GetProperty("content").GetString() ?? string.Empty;
            var content = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Replace("\n", string.Empty, StringComparison.Ordinal)));
            _files[(repository.Repository, path)] = (sha, content);
            read.Add(new InfrastructureFile(repository.Repository, path, repository.Kind, content, repository.Environment));
        }
        unreadable += skipped;
        return (read, unreadable == 0 ? null : $"{unreadable} files could not be read");
    }
}
