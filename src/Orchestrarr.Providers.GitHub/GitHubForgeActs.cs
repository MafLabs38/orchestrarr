using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orchestrarr.Core.Forge;

namespace Orchestrarr.Providers.GitHub;

/// <summary>
/// The merge of a pull request on GitHub, **squash**, on the head that the gateway authorised: GitHub refuses (409) when the branch has moved since, and the protections of the repository (the required check, the rulesets) still apply.
/// The token that does it is the control plane's own and goes to the configured address only; redirects are never followed. A runner never holds it.
/// </summary>
public sealed partial class GitHubForgeActs : IForgeActs
{
    private readonly HttpClient _http;

    private GitHubForgeActs(HttpClient http) => _http = http;

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9._-]{1,100}$", RegexOptions.None, 250)]
    private static partial Regex RepositoryName();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.None, 250)]
    private static partial Regex Sha();

    public static GitHubForgeActs Create(GitHubOptions options, HttpMessageHandler? handler = null)
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
        return new GitHubForgeActs(http);
    }

    public async Task<MergeResult> MergePullRequestAsync(string repositoryFullName, int number, string expectedHeadSha, CancellationToken cancellationToken)
    {
        if (!RepositoryName().IsMatch(repositoryFullName) || number < 1 || !Sha().IsMatch(expectedHeadSha))
        {
            return new MergeResult(false, null, "the repository, the pull request or the commit is not well formed");
        }
        try
        {
            using var response = await _http.PutAsJsonAsync(new Uri($"repos/{repositoryFullName}/pulls/{number}/merge", UriKind.Relative), new { sha = expectedHeadSha, merge_method = "squash" }, cancellationToken);
            using var body = await ReadAsync(response, cancellationToken);
            if (response.IsSuccessStatusCode && body?.RootElement.TryGetProperty("sha", out var sha) == true && sha.GetString() is { Length: > 0 } commit)
            {
                return new MergeResult(true, commit, null);
            }
            var message = body?.RootElement.TryGetProperty("message", out var text) == true ? text.GetString() : null;
            return new MergeResult(false, null, $"{(int)response.StatusCode}: {message ?? "the forge refused the merge"}");
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException)
        {
            // The merge may have been made before the answer was lost: it is said, and the next read of the pull request tells
            return new MergeResult(false, null, "the forge did not answer; the merge may have been made, the pull request is read again to know");
        }
    }

    private static async Task<JsonDocument?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
