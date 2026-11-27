using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Providers.GitHub;

/// <summary>Reads one pull request from GitHub's REST API: its head and base commits, the checks of the head and the paths that change. Read-only, redirects never followed, the token goes to the configured address only.</summary>
public sealed partial class GitHubPullRequestFacts : IPullRequestFacts, IDefaultBranchSource, IBranchSource, IPipelineFailures, IDependencyFindings
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
                var commits = await CommitAuthorsAsync(repositoryFullName, number, cancellationToken);
                var pushedBy = await HeadPushedByAsync(repositoryFullName, pull.RootElement.GetProperty("head"), head, cancellationToken);
                return new PullRequestFacts(head, baseSha, draft, checks, paths, complete, BaseRef: baseRef, DefaultBranch: defaultBranch, Author: author, Merge: mergeState, Closed: closed, BehindBy: behind,
                    CommitAuthors: commits, HeadPushedBy: pushedBy);
            }
            // Once it is merged, what runs is the commit that the merge made on the base branch: its tree and the checks of its pipeline are the facts of the later worlds
            var mergeChecks = await ChecksOfAsync(repositoryFullName, mergeCommit, [], cancellationToken, ownWorkflowsOnly: true);
            // A merge commit that is red turned the branch red only if the base before it was green (a push-only job that fails on every commit is not the merge's doing). A base that cannot be read, or whose pipeline is not over
            // or never ran, is not known: null, and the merge is not blamed for a red it may not have made
            bool? baseWasRed = false;
            if (mergeChecks == ChecksState.Failing)
            {
                var before = await FirstParentAsync(repositoryFullName, mergeCommit, cancellationToken) is { } parent ? await ChecksOfAsync(repositoryFullName, parent, [], cancellationToken, ownWorkflowsOnly: true) : ChecksState.Unknown;
                baseWasRed = before switch { ChecksState.Failing => true, ChecksState.Passing => false, _ => null };
            }
            return new PullRequestFacts(head, baseSha, draft, checks, paths, complete, Merged: true, mergeCommit, await TreeOfAsync(repositoryFullName, mergeCommit, cancellationToken), mergeChecks, baseRef, defaultBranch, BaseWasRed: baseWasRed);
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

    // The last run of each workflow of the repository on its default branch, newest first as the forge lists them; the forge's own jobs (`dynamic/...`) are not the repository's pipelines
    async Task<IReadOnlyList<string>> IPipelineFailures.ReadAsync(string repositoryFullName, CancellationToken cancellationToken)
    {
        if (await ReadAsync(repositoryFullName, cancellationToken) is not { } head)
        {
            return [];
        }
        try
        {
            using var runs = await GetAsync($"repos/{repositoryFullName}/actions/runs?branch={Uri.EscapeDataString(head.Branch)}&exclude_pull_requests=true&per_page=100", cancellationToken);
            if (runs is null || !runs.RootElement.TryGetProperty("workflow_runs", out var list))
            {
                return [];
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var failing = new List<string>();
            foreach (var run in list.EnumerateArray())
            {
                // Only the runs of the repository itself on its branch (a fork's pull request from a branch of the same name is someone else's text), and only the events that run the default branch
                if (!run.TryGetProperty("path", out var pathValue) || pathValue.GetString() is not { Length: > 0 } path || IsForgeServiceRun(path)
                    || !OwnRun(run, repositoryFullName) || run.GetProperty("status").GetString() != "completed" || !seen.Add(path))
                {
                    continue;
                }
                var conclusion = run.TryGetProperty("conclusion", out var ended) ? ended.GetString() : null;
                if (conclusion is "failure" or "timed_out" or "startup_failure" or "cancelled" or "action_required")
                {
                    var name = run.TryGetProperty("name", out var runName) ? runName.GetString() : null;
                    // Why it failed, as the forge recorded it (the failed step and the first error it annotated), for the first few: the worker repairs what failed, and the controller checks a repair against it (ADR 0057)
                    var why = failing.Count < MaxExplainedFailures && run.TryGetProperty("id", out var runId) && runId.TryGetInt64(out var id) ? await WhyItFailedAsync(repositoryFullName, id, cancellationToken) : null;
                    failing.Add(Plain($"pipeline {path}{(name is { Length: > 0 } ? $" ({name})" : "")}: its last run on {head.Branch} ended {conclusion}{(why is null ? "" : $"; {why}")}"));
                }
            }
            return failing;
        }
        catch (Exception failure) when (failure is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException || failure is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    // The job a failure is in, which names the version of a matrix ("test (16)")
    private static string InJob(string? jobName) => jobName is { Length: > 0 } ? $"in the job '{jobName}', " : string.Empty;

    // At most this many red pipelines are read for why they failed (two reads each)
    private const int MaxExplainedFailures = 10;

    // At most this much of the error the forge annotated is told
    private const int MaxErrorLength = 300;

    // The step of the first failed job, and the first error the forge annotated on that job: the text is the run's own output (the repository's text), told as data
    private async Task<string?> WhyItFailedAsync(string repositoryFullName, long runId, CancellationToken cancellationToken)
    {
        using var jobs = await GetAsync($"repos/{repositoryFullName}/actions/runs/{runId}/jobs?per_page=50", cancellationToken);
        if (jobs is null || !jobs.RootElement.TryGetProperty("jobs", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var job in list.EnumerateArray())
        {
            if (!job.TryGetProperty("conclusion", out var ended) || ended.GetString() is not ("failure" or "timed_out" or "cancelled") || !job.TryGetProperty("id", out var jobId) || !jobId.TryGetInt64(out var id))
            {
                continue;
            }
            var jobName = job.TryGetProperty("name", out var named) ? named.GetString() : null;
            var step = job.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array
                ? steps.EnumerateArray().Where(candidate => candidate.TryGetProperty("conclusion", out var stepEnded) && stepEnded.GetString() is "failure" or "timed_out" or "cancelled")
                    .Select(candidate => candidate.TryGetProperty("name", out var stepName) ? stepName.GetString() : null).FirstOrDefault()
                : null;
            using var annotations = await GetAsync($"repos/{repositoryFullName}/check-runs/{id}/annotations?per_page=20", cancellationToken);
            var error = annotations is { RootElement.ValueKind: JsonValueKind.Array }
                ? annotations.RootElement.EnumerateArray().Where(note => note.TryGetProperty("annotation_level", out var level) && level.GetString() == "failure")
                    .Select(note => note.TryGetProperty("message", out var message) ? message.GetString() : null).FirstOrDefault(message => message is { Length: > 0 })
                : null;
            if (error is null)
            {
                return step is null ? null : $"{InJob(jobName)}the step '{step}' failed";
            }
            var flat = error.ReplaceLineEndings(" ");
            return $"{InJob(jobName)}the step '{step ?? "unknown"}' failed: {flat[..Math.Min(flat.Length, MaxErrorLength)]}";
        }
        return null;
    }

    // The open security alerts of the forge on the dependencies of the repository (Dependabot's), one finding per package: the version that fixes every alert of the package (the highest of their first patched versions), the
    // highest severity and how many advisories. A package with an alert that has no patched version says so. Data for the lead, never an order; nothing that cannot be read is guessed
    async Task<IReadOnlyList<DependencyFinding>> IDependencyFindings.ReadAsync(string repository, CancellationToken cancellationToken)
    {
        if (!RepositoryName().IsMatch(repository))
        {
            return [];
        }
        try
        {
            using var alerts = await GetAsync($"repos/{repository}/dependabot/alerts?state=open&per_page=100", cancellationToken);
            if (alerts is null || alerts.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            var byPackage = new Dictionary<string, List<(string? Fix, string? Severity, string? Summary, string? Range)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var alert in alerts.RootElement.EnumerateArray())
            {
                if (!alert.TryGetProperty("dependency", out var dependency) || !dependency.TryGetProperty("package", out var package) || package.GetProperty("name").GetString() is not { Length: > 0 } name
                    || !alert.TryGetProperty("security_vulnerability", out var vulnerability))
                {
                    continue;
                }
                var fix = vulnerability.TryGetProperty("first_patched_version", out var patched) && patched.ValueKind == JsonValueKind.Object ? patched.GetProperty("identifier").GetString() : null;
                var range = vulnerability.TryGetProperty("vulnerable_version_range", out var vulnerable) ? vulnerable.GetString() : null;
                var advisory = alert.TryGetProperty("security_advisory", out var found) ? found : default;
                var severity = advisory.ValueKind == JsonValueKind.Object && advisory.TryGetProperty("severity", out var level) ? level.GetString() : null;
                var summary = advisory.ValueKind == JsonValueKind.Object && advisory.TryGetProperty("summary", out var text) ? text.GetString() : null;
                if (!byPackage.TryGetValue(name, out var list))
                {
                    byPackage[name] = list = [];
                }
                list.Add((fix, severity, summary, range));
            }
            return [.. byPackage.Take(50).Select(package => Finding(package.Key, package.Value))];
        }
        catch (Exception failure) when (failure is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException || failure is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    private static DependencyFinding Finding(string package, List<(string? Fix, string? Severity, string? Summary, string? Range)> alerts)
    {
        // One alert with no patched version leaves the package with no fixed version: a higher version does not remove that flaw
        var fix = alerts.Any(alert => alert.Fix is null) ? null : alerts.Select(alert => alert.Fix!).MaxBy(VersionKey, VersionComparer.Instance);
        var severity = alerts.Select(alert => alert.Severity).MaxBy(level => level switch { "critical" => 4, "high" => 3, "medium" => 2, "low" => 1, _ => 0 });
        var ranges = string.Join(", ", alerts.Select(alert => alert.Range).OfType<string>().Distinct(StringComparer.Ordinal).Take(3));
        var summary = Plain($"{alerts.Count} advisor{(alerts.Count == 1 ? "y" : "ies")} (vulnerable: {ranges}); {alerts[0].Summary}");
        return new DependencyFinding("advisory", package, null, fix, severity, summary, null);
    }

    // A version as numbers for the comparison (1.26.20 after 1.26.3); what is not a number sorts first
    private static int[] VersionKey(string version) =>
        [.. version.Split('.', '-', '+').Select(part => int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : -1)];

    private sealed class VersionComparer : IComparer<int[]>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(int[]? x, int[]? y)
        {
            x ??= [];
            y ??= [];
            for (var index = 0; index < Math.Max(x.Length, y.Length); index++)
            {
                var difference = (index < x.Length ? x[index] : 0).CompareTo(index < y.Length ? y[index] : 0);
                if (difference != 0)
                {
                    return difference;
                }
            }
            return 0;
        }
    }

    private static readonly string[] BranchEvents = ["push", "workflow_dispatch", "schedule"];

    private static bool OwnRun(JsonElement run, string repository) =>
        run.TryGetProperty("event", out var trigger) && BranchEvents.Contains(trigger.GetString(), StringComparer.Ordinal)
        && run.TryGetProperty("head_repository", out var head) && head.ValueKind == JsonValueKind.Object && head.TryGetProperty("full_name", out var name)
        && string.Equals(name.GetString(), repository, StringComparison.OrdinalIgnoreCase);

    // One line of plain text, bounded: what the forge says is data for an agent, never a fence or a page
    private static string Plain(string value)
    {
        var flat = value.ReplaceLineEndings(" ").Replace("<", " ", StringComparison.Ordinal).Replace(">", " ", StringComparison.Ordinal).Trim();
        return flat.Length <= 300 ? flat : flat[..300];
    }

    private async Task<string?> FirstParentAsync(string repository, string sha, CancellationToken cancellationToken)
    {
        using var commit = await GetAsync($"repos/{repository}/commits/{sha}", cancellationToken);
        return commit is not null && commit.RootElement.TryGetProperty("parents", out var parents) && parents.ValueKind == JsonValueKind.Array && parents.GetArrayLength() > 0
            && parents[0].TryGetProperty("sha", out var parent) && parent.GetString() is { } found && Sha().IsMatch(found) ? found : null;
    }

    public async Task<FileAt?> FileAtAsync(string repositoryFullName, string sha, string path, CancellationToken cancellationToken)
    {
        if (!RepositoryName().IsMatch(repositoryFullName) || !Sha().IsMatch(sha) || !Orchestrarr.Core.Missions.RepairPolicy.IsPlainPath(path))
        {
            return null;
        }
        try
        {
            using var response = await _http.GetAsync(new Uri($"repos/{repositoryFullName}/contents/{path}?ref={sha}", UriKind.Relative), cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new FileAt(false, null);
            }
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            using var file = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            // A file over the forge's limit comes with no content: it was not read
            return file.RootElement.ValueKind == JsonValueKind.Object && file.RootElement.TryGetProperty("encoding", out var encoding) && encoding.GetString() == "base64"
                && file.RootElement.TryGetProperty("content", out var content) && content.GetString() is { } text
                ? new FileAt(true, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(text.Replace("\n", string.Empty, StringComparison.Ordinal))))
                : null;
        }
        catch (Exception failure) when (failure is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException || failure is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    // A tree of more files than this is not listed: a change of a path filter on it is not measured, and is refused
    private const int MaxListedFiles = 100_000;

    public async Task<IReadOnlyList<string>?> FilesAtAsync(string repositoryFullName, string sha, CancellationToken cancellationToken)
    {
        if (!RepositoryName().IsMatch(repositoryFullName) || !Sha().IsMatch(sha))
        {
            return null;
        }
        try
        {
            using var tree = await GetAsync($"repos/{repositoryFullName}/git/trees/{sha}?recursive=1", cancellationToken);
            // A tree the forge cut short is not the list of the files: what it leaves out could be what a filter covered
            if (tree is null || tree.RootElement.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True
                || !tree.RootElement.TryGetProperty("tree", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > MaxListedFiles)
            {
                return null;
            }
            return [.. entries.EnumerateArray().Where(entry => entry.TryGetProperty("type", out var type) && type.GetString() == "blob")
                .Select(entry => entry.TryGetProperty("path", out var path) ? path.GetString() : null).OfType<string>()];
        }
        catch (Exception failure) when (failure is HttpRequestException or JsonException or InvalidOperationException || failure is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

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
    private async Task<ChecksState> ChecksOfAsync(string repository, string sha, IReadOnlyList<string> required, CancellationToken cancellationToken, bool ownWorkflowsOnly = false)
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
        // The commit of a merge is judged on the pipelines of the repository itself (its workflows), not on what another app reports on it: a red integration branch is what sends the problem back to fix it forward (ADR 0055)
        if (ownWorkflowsOnly && await OwnSuitesAsync(repository, sha, cancellationToken) is { } own)
        {
            judged = [.. judged.Where(run => run.Suite is { } suite && own.Contains(suite))];
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

    // The check suites of a commit that the repository's own workflows made (`.github/workflows/`, on the repository itself); null when they cannot be read (then nothing is left out)
    private async Task<HashSet<long>?> OwnSuitesAsync(string repository, string sha, CancellationToken cancellationToken)
    {
        using var document = await GetAsync($"repos/{repository}/actions/runs?head_sha={sha}&per_page=100", cancellationToken);
        if (document is null || !document.RootElement.TryGetProperty("workflow_runs", out var list))
        {
            return null;
        }
        var suites = new HashSet<long>();
        foreach (var run in list.EnumerateArray())
        {
            if (run.TryGetProperty("path", out var path) && path.GetString() is { } file && file.StartsWith(".github/workflows/", StringComparison.Ordinal)
                && run.TryGetProperty("head_repository", out var head) && head.ValueKind == JsonValueKind.Object && head.TryGetProperty("full_name", out var name) && string.Equals(name.GetString(), repository, StringComparison.OrdinalIgnoreCase)
                && run.TryGetProperty("check_suite_id", out var suite) && suite.TryGetInt64(out var id))
            {
                _ = suites.Add(id);
            }
        }
        return suites;
    }

    private static bool IsForgeServiceRun(string path) => path.StartsWith("dynamic/dependabot/", StringComparison.Ordinal);

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
            if (run.TryGetProperty("path", out var path) && path.GetString() is { } file && IsForgeServiceRun(file)
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

    // The account each commit is linked to, as the forge links it (from the e-mail that whoever wrote the commit chose: it says who claims to have written it, not who pushed it)
    private async Task<IReadOnlyList<string>?> CommitAuthorsAsync(string repository, int number, CancellationToken cancellationToken)
    {
        using var commits = await GetAsync($"repos/{repository}/pulls/{number}/commits?per_page=100", cancellationToken);
        return commits is null || commits.RootElement.ValueKind != JsonValueKind.Array || commits.RootElement.GetArrayLength() >= 100
            ? null
            : [.. commits.RootElement.EnumerateArray().Select(commit => LoginOf(commit, "author"))];
    }

    // Who pushed the head to its branch, as the forge recorded the push with the account that was signed in (its activity log): no one can write that in a commit. A branch of another repository (a fork), a head that no
    // push of the log produced, or a log that cannot be read, is no one's
    private async Task<string?> HeadPushedByAsync(string repository, JsonElement headOfPull, string head, CancellationToken cancellationToken)
    {
        if (!headOfPull.TryGetProperty("ref", out var refName) || refName.GetString() is not { Length: > 0 and <= 200 } branch
            || !headOfPull.TryGetProperty("repo", out var headRepository) || headRepository.ValueKind != JsonValueKind.Object
            || !headRepository.TryGetProperty("full_name", out var fullName) || !string.Equals(fullName.GetString(), repository, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var fullRef = $"refs/heads/{branch}";
        using var activity = await GetAsync($"repos/{repository}/activity?ref={Uri.EscapeDataString(fullRef)}&per_page=100", cancellationToken);
        if (activity is null || activity.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        // Newest first: the push that made the head is the newest one on this branch whose result is the head
        var push = activity.RootElement.EnumerateArray().FirstOrDefault(item =>
            item.TryGetProperty("ref", out var itemRef) && string.Equals(itemRef.GetString(), fullRef, StringComparison.Ordinal)
            && item.TryGetProperty("after", out var after) && string.Equals(after.GetString(), head, StringComparison.Ordinal));
        return push.ValueKind == JsonValueKind.Object && LoginOf(push, "actor") is { Length: > 0 } actor ? actor : null;
    }

    private static string LoginOf(JsonElement commit, string role) =>
        commit.TryGetProperty(role, out var account) && account.ValueKind == JsonValueKind.Object && account.TryGetProperty("login", out var login) ? login.GetString() ?? string.Empty : string.Empty;

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
