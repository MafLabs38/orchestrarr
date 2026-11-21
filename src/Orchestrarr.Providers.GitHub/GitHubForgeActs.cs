using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Missions;

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

    [GeneratedRegex(@"^orchestrarr/[a-z0-9][a-z0-9-]{0,59}$", RegexOptions.None, 250)]
    private static partial Regex BranchName();

    public async Task<OpenedPullRequest> OpenPullRequestAsync(string repositoryFullName, string baseBranch, string parentSha, string branch, IReadOnlyList<PatchedFile> files, string title, string body, CancellationToken cancellationToken)
    {
        if (!RepositoryName().IsMatch(repositoryFullName) || !Sha().IsMatch(parentSha) || !BranchName().IsMatch(branch) || string.IsNullOrWhiteSpace(baseBranch) || baseBranch.Length > 100 || files.Count == 0)
        {
            return new OpenedPullRequest(false, 0, null, "the repository, the commit, the branch or the files are not well formed");
        }
        try
        {
            // The branch is made from the tip of the base branch, the change under repair is merged into it (so that the branch carries the base's own pipeline, as the staging proved the merge result), and one commit puts the repair
            // on top of that merge: the files are written whole, nothing else is touched, and the branch is a new name (a name that exists is refused by the forge)
            var tip = await SendAsync(HttpMethod.Get, $"repos/{repositoryFullName}/git/ref/heads/{Uri.EscapeDataString(baseBranch)}", null, cancellationToken);
            var baseTip = tip.Body?.RootElement.TryGetProperty("object", out var tipObject) == true && tipObject.TryGetProperty("sha", out var tipSha) ? tipSha.GetString() : null;
            if (baseTip is null)
            {
                return Failed("the base branch cannot be read", tip);
            }
            var reference = await SendAsync(HttpMethod.Post, $"repos/{repositoryFullName}/git/refs", new { @ref = $"refs/heads/{branch}", sha = baseTip }, cancellationToken);
            if (!reference.Ok)
            {
                return Failed("the branch cannot be made", reference);
            }
            var merge = await SendAsync(HttpMethod.Post, $"repos/{repositoryFullName}/merges", new { @base = branch, head = parentSha, commit_message = $"Merge the change to repair into {branch}" }, cancellationToken);
            var mergeCommit = ShaOf(merge) ?? baseTip;
            if (!merge.Ok && merge.Status != 204)
            {
                return Failed("the change cannot be merged into the branch (a conflict with the base)", merge);
            }
            var parent = await SendAsync(HttpMethod.Get, $"repos/{repositoryFullName}/git/commits/{mergeCommit}", null, cancellationToken);
            var baseTree = parent.Body?.RootElement.TryGetProperty("tree", out var tree) == true && tree.TryGetProperty("sha", out var treeSha) ? treeSha.GetString() : null;
            if (baseTree is null)
            {
                return Failed("the merged commit cannot be read", parent);
            }
            var made = await SendAsync(HttpMethod.Post, $"repos/{repositoryFullName}/git/trees", new { base_tree = baseTree, tree = files.Select(file => new { path = file.Path, mode = "100644", type = "blob", content = file.Content }) }, cancellationToken);
            if (ShaOf(made) is not { } newTree)
            {
                return Failed("the tree cannot be made", made);
            }
            var commit = await SendAsync(HttpMethod.Post, $"repos/{repositoryFullName}/git/commits", new { message = title, tree = newTree, parents = new[] { mergeCommit } }, cancellationToken);
            if (ShaOf(commit) is not { } head)
            {
                return Failed("the commit cannot be made", commit);
            }
            var moved = await SendAsync(new HttpMethod("PATCH"), $"repos/{repositoryFullName}/git/refs/heads/{branch}", new { sha = head }, cancellationToken);
            if (!moved.Ok)
            {
                return Failed("the branch cannot be moved to the repair", moved);
            }
            var pull = await SendAsync(HttpMethod.Post, $"repos/{repositoryFullName}/pulls", new { title, head = branch, @base = baseBranch, body }, cancellationToken);
            return pull.Ok && pull.Body?.RootElement.TryGetProperty("number", out var number) == true && number.TryGetInt32(out var n) ? new OpenedPullRequest(true, n, head, null) : Failed("the pull request cannot be opened", pull);
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new OpenedPullRequest(false, 0, null, "the forge did not answer; the branch may have been made");
        }
    }

    public async Task<bool> ClosePullRequestAsync(string repositoryFullName, int number, string comment, CancellationToken cancellationToken)
    {
        if (!RepositoryName().IsMatch(repositoryFullName) || number < 1)
        {
            return false;
        }
        try
        {
            _ = await SendAsync(HttpMethod.Post, $"repos/{repositoryFullName}/issues/{number}/comments", new { body = comment }, cancellationToken);
            return (await SendAsync(new HttpMethod("PATCH"), $"repos/{repositoryFullName}/pulls/{number}", new { state = "closed" }, cancellationToken)).Ok;
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    private sealed record Answer(bool Ok, int Status, JsonDocument? Body);

    private async Task<Answer> SendAsync(HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }
        using var response = await _http.SendAsync(request, cancellationToken);
        return new Answer(response.IsSuccessStatusCode, (int)response.StatusCode, await ReadAsync(response, cancellationToken));
    }

    private static string? ShaOf(Answer answer) => answer.Ok && answer.Body?.RootElement.TryGetProperty("sha", out var sha) == true ? sha.GetString() : null;

    private static OpenedPullRequest Failed(string what, Answer answer) =>
        new(false, 0, null, $"{what} ({answer.Status}: {(answer.Body?.RootElement.TryGetProperty("message", out var message) == true ? message.GetString() : "no message")})");

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
