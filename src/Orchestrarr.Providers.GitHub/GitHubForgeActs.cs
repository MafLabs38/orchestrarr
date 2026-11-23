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

    private readonly TimeSpan _retryDelay;

    private GitHubForgeActs(HttpClient http, TimeSpan retryDelay)
    {
        _http = http;
        _retryDelay = retryDelay;
    }

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9._-]{1,100}$", RegexOptions.None, 250)]
    private static partial Regex RepositoryName();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.None, 250)]
    private static partial Regex Sha();

    public static GitHubForgeActs Create(GitHubOptions options, HttpMessageHandler? handler = null, TimeSpan? retryDelay = null)
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
        return new GitHubForgeActs(http, retryDelay ?? TimeSpan.FromSeconds(2));
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
            using var tip = await SendAsync(HttpMethod.Get, $"repos/{repositoryFullName}/git/ref/heads/{Uri.EscapeDataString(baseBranch)}", null, cancellationToken);
            var baseTip = tip.Body?.RootElement.TryGetProperty("object", out var tipObject) == true && tipObject.TryGetProperty("sha", out var tipSha) ? tipSha.GetString() : null;
            if (baseTip is null)
            {
                return Failed("the base branch cannot be read", tip);
            }
            using var reference = await SendAsync(HttpMethod.Post, $"repos/{repositoryFullName}/git/refs", new { @ref = $"refs/heads/{branch}", sha = baseTip }, cancellationToken);
            if (!reference.Ok)
            {
                return Failed("the branch cannot be made", reference);
            }
            using var merge = await SendAsync(HttpMethod.Post, $"repos/{repositoryFullName}/merges", new { @base = branch, head = parentSha, commit_message = $"Merge the change to repair into {branch}" }, cancellationToken);
            var mergeCommit = ShaOf(merge) ?? baseTip;
            if (!merge.Ok && merge.Status != 204)
            {
                return Failed("the change cannot be merged into the branch (a conflict with the base)", merge);
            }
            using var parent = await SendAsync(HttpMethod.Get, $"repos/{repositoryFullName}/git/commits/{mergeCommit}", null, cancellationToken);
            var baseTree = parent.Body?.RootElement.TryGetProperty("tree", out var tree) == true && tree.TryGetProperty("sha", out var treeSha) ? treeSha.GetString() : null;
            if (baseTree is null)
            {
                return Failed("the merged commit cannot be read", parent);
            }
            using var made = await SendAsync(HttpMethod.Post, $"repos/{repositoryFullName}/git/trees", new { base_tree = baseTree, tree = files.Select(file => new { path = file.Path, mode = "100644", type = "blob", content = file.Content }) }, cancellationToken);
            if (ShaOf(made) is not { } newTree)
            {
                return Failed("the tree cannot be made", made);
            }
            using var commit = await SendAsync(HttpMethod.Post, $"repos/{repositoryFullName}/git/commits", new { message = title, tree = newTree, parents = new[] { mergeCommit } }, cancellationToken);
            if (ShaOf(commit) is not { } head)
            {
                return Failed("the commit cannot be made", commit);
            }
            using var moved = await SendAsync(new HttpMethod("PATCH"), $"repos/{repositoryFullName}/git/refs/heads/{branch}", new { sha = head }, cancellationToken);
            if (!moved.Ok)
            {
                return Failed("the branch cannot be moved to the repair", moved);
            }
            using var pull = await SendAsync(HttpMethod.Post, $"repos/{repositoryFullName}/pulls", new { title, head = branch, @base = baseBranch, body }, cancellationToken);
            return pull.Ok && pull.Body?.RootElement.TryGetProperty("number", out var number) == true && number.TryGetInt32(out var n) ? new OpenedPullRequest(true, n, head, null) : Failed("the pull request cannot be opened", pull);
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new OpenedPullRequest(false, 0, null, "the forge did not answer; the branch may have been made");
        }
    }

    public async Task<OpenedPullRequest> OpenPullRequestFromBranchAsync(string repositoryFullName, string baseBranch, string branch, string expectedSha, string title, string body, CancellationToken cancellationToken)
    {
        if (!RepositoryName().IsMatch(repositoryFullName) || !Sha().IsMatch(expectedSha) || !AgentBranch.IsName(branch) || string.IsNullOrWhiteSpace(baseBranch) || baseBranch.Length > 100)
        {
            return new OpenedPullRequest(false, 0, null, "the repository, the branch or the commit is not well formed");
        }
        try
        {
            // The branch is the agent's: the gateway opens a pull request from it only if it still is at the commit that was audited (the agent may have pushed again after it said it had finished)
            using var tip = await SendAsync(HttpMethod.Get, $"repos/{repositoryFullName}/git/ref/heads/{branch}", null, cancellationToken);
            var now = tip.Body?.RootElement.TryGetProperty("object", out var target) == true && target.TryGetProperty("sha", out var sha) ? sha.GetString() : null;
            if (!string.Equals(now, expectedSha, StringComparison.Ordinal))
            {
                return new OpenedPullRequest(false, 0, null, "the branch is not at the commit that was audited: the agent pushed again, or the branch is gone");
            }
            // The forge answers a 5xx now and then, and the pull request may even have been made before the answer was lost: a few tries, each preceded by a look for the pull request that the branch already carries
            Answer pull = await SendAsync(HttpMethod.Post, $"repos/{repositoryFullName}/pulls", new { title, head = branch, @base = baseBranch, body }, cancellationToken);
            for (var attempt = 1; attempt < OpenAttempts && pull.Status >= 500; attempt++)
            {
                pull.Dispose();
                await Task.Delay(_retryDelay * attempt, cancellationToken);
                var owner = repositoryFullName[..repositoryFullName.IndexOf('/', StringComparison.Ordinal)];
                using var existing = await SendAsync(HttpMethod.Get, $"repos/{repositoryFullName}/pulls?state=open&head={Uri.EscapeDataString($"{owner}:{branch}")}", null, cancellationToken);
                if (existing.Ok && existing.Body?.RootElement is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0)
                {
                    pull = new Answer(true, 200, JsonDocument.Parse(list[0].GetRawText()));
                    break;
                }
                pull = await SendAsync(HttpMethod.Post, $"repos/{repositoryFullName}/pulls", new { title, head = branch, @base = baseBranch, body }, cancellationToken);
            }
            using var held = pull;
            if (!pull.Ok || pull.Body?.RootElement.TryGetProperty("number", out var number) != true || !number.TryGetInt32(out var n))
            {
                return Failed("the pull request cannot be opened", pull);
            }
            // The agent could have pushed between the read of the branch and the opening: the pull request is of the commit that was audited or it is closed at once
            var opened = pull.Body.RootElement.TryGetProperty("head", out var head) && head.TryGetProperty("sha", out var openedSha) ? openedSha.GetString() : null;
            if (!string.Equals(opened, expectedSha, StringComparison.Ordinal))
            {
                using var closed = await SendAsync(new HttpMethod("PATCH"), $"repos/{repositoryFullName}/pulls/{n}", new { state = "closed" }, cancellationToken);
                return new OpenedPullRequest(false, 0, null, "the branch moved while the pull request was opened: it was closed, the commit is not the one that was audited");
            }
            return new OpenedPullRequest(true, n, expectedSha, null);
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new OpenedPullRequest(false, 0, null, "the forge did not answer");
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
            using var commented = await SendAsync(HttpMethod.Post, $"repos/{repositoryFullName}/issues/{number}/comments", new { body = comment }, cancellationToken);
            using var closed = await SendAsync(new HttpMethod("PATCH"), $"repos/{repositoryFullName}/pulls/{number}", new { state = "closed" }, cancellationToken);
            return closed.Ok;
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    private const int OpenAttempts = 3;

    private sealed record Answer(bool Ok, int Status, JsonDocument? Body) : IDisposable
    {
        public void Dispose() => Body?.Dispose();
    }

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
