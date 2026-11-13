using Orchestrarr.Core.Applications;
using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Infrastructure;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Decisions;

/// <summary>How far a problem can go by itself (ADR 0019): the lowest of what the description allows, what the runners accept and the state of the problem.</summary>
public enum Ceiling
{
    /// <summary>Nothing is defined for this problem: it is shown, and cannot be selected.</summary>
    NoAction,

    /// <summary>Built and tested in the workshop; nothing is merged.</summary>
    Qualification,
    MergeIntoDevelop,
    Production,

    /// <summary>The pull request fails (on the forge, or when the workshop ran it): the next thing is to repair it. An agent tries, and the patch is shown; nothing is pushed.</summary>
    Repair,
}

/// <summary>Why a problem stops where it does. The interface words each one.</summary>
public enum CeilingReason
{
    /// <summary>It goes as far as the chain goes: nothing stops it.</summary>
    None,
    NoDescription,
    NoActionDefined,
    PullRequestsNotCurrent,
    NotGranted,
    NoRunner,
    QualificationNotDescribed,
    ChecksFailing,
    ChecksNotPassing,
    CannotBeMergedNow,
    NoRollbackDescribed,
    NoCheckAfterDeployDescribed,
    UpdateClassStopsBeforeProduction,

    /// <summary>The workshop ran this commit and the build or the tests failed.</summary>
    QualificationFailed,

    /// <summary>An agent tried to repair this commit and did not succeed: it can be tried again.</summary>
    RepairFailed,

    /// <summary>An agent repaired this commit and a patch is in the trace of its run. Applying it is a write power that is not built yet.</summary>
    PatchReady,

    /// <summary>The workshop passes this commit while the forge's checks fail: the pipeline of the repository is what to look at.</summary>
    PassesHereChecksFailing,

    /// <summary>The checks could not run for a bot's pull request (they need secrets): the Runner's validation is what counts.</summary>
    ChecksNotRun,

    /// <summary>A version of Node that is not a long-term release: it is not proposed.</summary>
    NotLongTermSupport,

    /// <summary>The image comes with the platform (K3s): it is updated with it, not on its own.</summary>
    ManagedByPlatform,

    /// <summary>A new major version of an image: not a tag to bump, a change to read about and to plan.</summary>
    MajorNeedsPlan,

    /// <summary>The image is written in no repository of the description: add the repository that deploys it to the description, or leave it out of the scope.</summary>
    NotInScope,

    /// <summary>The line that writes the version is rewritten by an automation (Flux): it is not ours to change.</summary>
    ManagedByAutomation,

    /// <summary>The code says one version and another one runs: the next deployment would change something nobody decided.</summary>
    CodeDiffersFromRunning,

    /// <summary>The reference is pinned by a digest: the pin is a decision, and a tag would put a moving reference in its place. The place is shown, no change is proposed.</summary>
    PinnedByDigest,

    /// <summary>A repository of the description could not be read: the image may be written there, so it is not said to be written nowhere.</summary>
    CodeNotFullyRead,

    /// <summary>The image runs in several environments: a change is made for one at a time, qualified and then promoted, so none is proposed for all at once.</summary>
    SeveralEnvironments,

}

/// <summary>What the workshop last said about this very commit of a pull request, for the column that sits beside the forge's checks.</summary>
public enum WorkshopVerdict
{
    /// <summary>The workshop never ran this commit.</summary>
    Unknown,

    Passes,

    Fails,

    /// <summary>An agent repaired it: the patch is in the trace of its run.</summary>
    Repaired,
}

/// <summary>The kinds of problem the table shows (one row each).</summary>
public enum ProblemKind
{
    DependencyUpdate,
    DependencyAlert,
    CodeScanningAlert,
    ExposedSecret,
    FailingCi,
    ImageUpdate,
    DependencyScanUnavailable,
    PullRequestsNotRead,

    /// <summary>The image is run under a tag that moves: pin a version.</summary>
    FloatingImage,

    /// <summary>No push for a long time and the CI is red: the repository is probably dead, and its pull requests cannot pass. Archive it or fix it.</summary>
    DormantRepository,
}

/// <summary>
/// One problem, one row (ADR 0019). A repository is only an attribute. <c>PullRequest</c> and <c>HeadSha</c> bind a dependency update to the commit that
/// was read; <c>Severity</c> is known for an alert and not for an update.
/// </summary>
public sealed record ProblemRow(
    string Id,
    string WorkItemId,
    string Application,
    ProblemKind Kind,
    string Description,
    Severity? Severity,
    int? Count,
    UpdateKind? UpdateClass,
    ChecksState? Checks,
    Uri? Url,
    int? PullRequest,
    string? HeadSha,
    Ceiling GoesUpTo,
    CeilingReason Why,
    bool CanStopBeforeProduction,
    WorkshopVerdict Workshop = WorkshopVerdict.Unknown,
    bool InProgress = false,
    string? ApplicationId = null,
    string? Icon = null,
    string? Source = null,
    IReadOnlyList<CodeLocation>? Locations = null,
    IReadOnlyList<RepositoryProblem>? Warnings = null);

/// <summary>What a connected runner accepts, as it reported it. The table never promises more than they would run: with none connected, nothing can be run, and every row says so.</summary>
public sealed record RunnerAcceptance(IReadOnlyList<TaskKind> Kinds, IReadOnlyList<string> Repositories)
{
    public bool Accepts(TaskKind kind, string repository) => Kinds.Contains(kind) && Repositories.Any(pattern => RepositoryPattern.Matches(pattern, repository));
}

/// <summary>Builds the rows of the table from the work items, the descriptions and the runners. A pure function: the same input gives the same table.</summary>
public static class DecisionTable
{
    public static IReadOnlyList<ProblemRow> Build(
        IReadOnlyList<WorkItem> items, IApplicationCatalog applications, IReadOnlyList<RunnerAcceptance> runners, IReadOnlyList<TaskResultRow>? results = null,
        IReadOnlyList<ApplicationInfo>? applicationNames = null, DateTimeOffset? now = null, RelevancePolicy? policy = null, InfrastructureIndex? infrastructure = null)
    {
        policy ??= RelevancePolicy.Default;
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(applications);
        ArgumentNullException.ThrowIfNull(runners);
        var rows = new List<ProblemRow>();
        foreach (var item in items)
        {
            var before = rows.Count;
            if (DormantDays(item, now, policy) is { } days && item.LastPushAt is { } lastPush)
            {
                // One row instead of the pull requests and the red build of a repository that nobody works on: the decision is about the repository. The text names the day
                // of the last push, not the number of days (that one changes every day, and a skip lasts as long as the text stays the same). What is a risk stays as its own
                // row (an exposed secret, an alert, a scan that cannot be read): a dead repository can still leak.
                rows.Add(Of(item, $"dormant:{item.Id}", ProblemKind.DormantRepository, $"last push {lastPush.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}", Severity.Info, days, Ceiling.NoAction, CeilingReason.NoActionDefined, item.RepositoryFullName));
                AddOtherProblems(rows, item, policy, infrastructure, dormant: true);
                NameApplication(rows, before, item, applicationNames ?? []);
                Warn(rows, before, infrastructure);
                continue;
            }
            AddDependencyUpdates(rows, item, applications, runners, results ?? [], policy);
            AddOtherProblems(rows, item, policy, infrastructure);
            NameApplication(rows, before, item, applicationNames ?? []);
            Warn(rows, before, infrastructure);
        }
        return rows;
    }

    /// <summary>The start of the identifier of a row about a pull request: such a row exists only while the pull requests of its repository were read, all of them.</summary>
    public const string PullRequestRowPrefix = "pr:";

    // No push for longer than the policy says, with a red build: the repository is taken for dead
    private static int? DormantDays(WorkItem item, DateTimeOffset? now, RelevancePolicy policy)
    {
        if (now is not { } at || item.LastPushAt is not { } lastPush || !item.Findings.Any(finding => finding.Kind == FindingKind.FailingCi))
        {
            return null;
        }
        var days = (int)(at - lastPush).TotalDays;
        return days > policy.DormantAfterDays ? days : null;
    }

    // What the bell says about a repository that cannot be read is also said on the rows it concerns: the rows about that very repository, and the rows that stopped
    // because the code was not fully read (the version may be written in any of them, so all are named)
    private static void Warn(List<ProblemRow> rows, int from, InfrastructureIndex? infrastructure)
    {
        if (infrastructure is not { Problems.Count: > 0 })
        {
            return;
        }
        for (var i = from; i < rows.Count; i++)
        {
            var row = rows[i];
            var concerned = row.Why == CeilingReason.CodeNotFullyRead
                ? infrastructure.Problems
                : [.. infrastructure.Problems.Where(problem => string.Equals(problem.Repository, row.Source, StringComparison.Ordinal))];
            if (concerned.Count > 0)
            {
                rows[i] = row with { Warnings = concerned };
            }
        }
    }

    // The application a person knows: when the source says that the repository or the image is one application, its name and its icon; otherwise the repository
    // or the image itself. The repository or the image stays in <c>Source</c>.
    private static void NameApplication(List<ProblemRow> rows, int from, WorkItem item, IReadOnlyList<ApplicationInfo> names)
    {
        var known = item.AppIds.Distinct(StringComparer.Ordinal).Select(id => names.FirstOrDefault(name => name.Id == id)).OfType<ApplicationInfo>().ToList();
        for (var i = from; i < rows.Count; i++)
        {
            var source = rows[i].Application;
            rows[i] = known.Count == 1
                ? rows[i] with { Application = known[0].Name, ApplicationId = known[0].Id, Icon = known[0].Icon, Source = source }
                : rows[i] with { Source = source };
        }
    }

    // The steps a ceiling stands for: the runner validates, and merges from the merge ceiling up. Production is a decision recorded with the approval.
    public static IReadOnlyList<StepKind> StepsOf(Ceiling ceiling) => ceiling switch
    {
        Ceiling.Qualification => [StepKind.Validate],
        Ceiling.MergeIntoDevelop or Ceiling.Production => [StepKind.Validate, StepKind.MergePullRequests],
        Ceiling.Repair => [StepKind.Repair],
        _ => [],
    };

    private static void AddDependencyUpdates(List<ProblemRow> rows, WorkItem item, IApplicationCatalog applications, IReadOnlyList<RunnerAcceptance> runners, IReadOnlyList<TaskResultRow> results, RelevancePolicy policy)
    {
        if (item.RepositoryFullName is null)
        {
            return;
        }
        var reads = item.PullRequests;
        var waiting = item.Findings.FirstOrDefault(finding => finding.Kind == FindingKind.OpenDependencyPullRequests);
        if (waiting is not null && reads.Items.Count == 0 && reads.Status is PullRequestsStatus.Pending or PullRequestsStatus.Failed or PullRequestsStatus.NotConnected)
        {
            // The portal says pull requests wait, and the forge did not say which: the rows cannot be made, and the table says so
            rows.Add(Of(item, $"pullrequests:{item.Id}", ProblemKind.PullRequestsNotRead, waiting.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), Severity.Info, waiting.Count, Ceiling.NoAction, CeilingReason.PullRequestsNotCurrent));
        }
        foreach (var pullRequest in reads.Items.Where(pullRequest => !pullRequest.Draft))
        {
            var (ceiling, why, canStop) = CeilingOf(item, pullRequest, applications, runners);
            if (IsNotLongTerm(pullRequest, policy))
            {
                (ceiling, why, canStop) = (Ceiling.NoAction, CeilingReason.NotLongTermSupport, false);
            }
            var mine = results.Where(row => string.Equals(row.Repository, item.RepositoryFullName, StringComparison.OrdinalIgnoreCase) && row.PullRequest == pullRequest.Number && string.Equals(row.HeadSha, pullRequest.HeadSha, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(row => row.At).ToList();
            (ceiling, why, canStop) = WorkshopWord(item, pullRequest, ceiling, why, canStop, runners, mine);
            var change = pullRequest.Changes.Count == 1 ? pullRequest.Changes[0] : null;
            var description = change is { From: not null, To: not null } ? $"{change.Package} {change.From} → {change.To}" : pullRequest.Title;
            rows.Add(new ProblemRow(
                $"{PullRequestRowPrefix}{item.RepositoryFullName}#{pullRequest.Number}", item.Id, item.RepositoryFullName, ProblemKind.DependencyUpdate, description,
                null, null, change?.Kind ?? UpdateKind.Unknown, pullRequest.Checks, pullRequest.Url, pullRequest.Number, pullRequest.HeadSha, ceiling, why, canStop, VerdictOf(mine)));
        }
    }

    // The next thing for a pull request is not always the next level: when it fails, on the forge or in the workshop, it is to repair it. The workshop's
    // latest word on this very commit counts (a new commit starts again): a failure makes the row a repair, a repair that worked leaves a patch to apply.
    private static (Ceiling Ceiling, CeilingReason Why, bool CanStop) WorkshopWord(
        WorkItem item, PullRequestInfo pullRequest, Ceiling ceiling, CeilingReason why, bool canStop, IReadOnlyList<RunnerAcceptance> runners, IReadOnlyList<TaskResultRow> mine)
    {
        if (ceiling == Ceiling.NoAction)
        {
            return (ceiling, why, canStop);
        }
        var repository = item.RepositoryFullName!;
        var canRepair = item.Plan.FirstOrDefault(step => step.Kind == StepKind.Repair && step.EnvironmentId is null) is { Available: true, Granted: true }
            && runners.Any(runner => runner.Accepts(TaskKind.RepairPullRequest, repository));
        var latest = LatestWord(mine);
        if (latest is { Result: TaskResult.Repaired })
        {
            return (Ceiling.NoAction, CeilingReason.PatchReady, false);
        }
        if (latest is { Result: TaskResult.Failed })
        {
            var reason = latest.Kind == TaskKind.RepairPullRequest ? CeilingReason.RepairFailed : CeilingReason.QualificationFailed;
            return canRepair ? (Ceiling.Repair, reason, false) : (Ceiling.Qualification, reason, false);
        }
        if (pullRequest.Checks == ChecksState.Failing)
        {
            if (latest is { Result: TaskResult.Passed })
            {
                return (Ceiling.Qualification, CeilingReason.PassesHereChecksFailing, false);
            }
            if (canRepair)
            {
                return (Ceiling.Repair, CeilingReason.ChecksFailing, false);
            }
        }
        return (ceiling, why, canStop);
    }

    // The workshop's latest word on the commit (newest first in <paramref name="mine"/>): the last validation or repair that ended with a verdict. A task that was
    // refused or skipped said nothing about the code, so an older failure still counts.
    private static TaskResultRow? LatestWord(IReadOnlyList<TaskResultRow> mine) =>
        mine.FirstOrDefault(row => row.Kind is TaskKind.ValidatePullRequest or TaskKind.RepairPullRequest && row.Result is TaskResult.Passed or TaskResult.Failed or TaskResult.Repaired);

    private static WorkshopVerdict VerdictOf(IReadOnlyList<TaskResultRow> mine) => LatestWord(mine)?.Result switch
    {
        TaskResult.Repaired => WorkshopVerdict.Repaired,
        TaskResult.Passed => WorkshopVerdict.Passes,
        TaskResult.Failed => WorkshopVerdict.Fails,
        _ => WorkshopVerdict.Unknown,
    };

    // A package that is moved only to an even major version (Node: the odd ones are never long-term releases, 23, 25…) is not moved to an odd one
    private static bool IsNotLongTerm(PullRequestInfo pullRequest, RelevancePolicy policy) =>
        pullRequest.Changes is [{ To: { } to } change] && policy.EvenMajorOnlyPackages.Contains(change.Package, StringComparer.OrdinalIgnoreCase)
        && int.TryParse(to.TrimStart('v', 'V').Split('-')[0].Split('.')[0], System.Globalization.CultureInfo.InvariantCulture, out var major) && major % 2 == 1;

    private static (Ceiling Ceiling, CeilingReason Why, bool CanStop) CeilingOf(WorkItem item, PullRequestInfo pullRequest, IApplicationCatalog applications, IReadOnlyList<RunnerAcceptance> runners)
    {
        var repository = item.RepositoryFullName!;
        if (item.PullRequests.Stale || item.PullRequests.Status != PullRequestsStatus.Read || string.IsNullOrEmpty(pullRequest.HeadSha))
        {
            return (Ceiling.NoAction, CeilingReason.PullRequestsNotCurrent, false);
        }
        var validate = item.Plan.FirstOrDefault(step => step.Kind == StepKind.Validate && step.EnvironmentId is null);
        if (validate is not { Available: true, Granted: true })
        {
            return (Ceiling.NoAction, CeilingReason.NotGranted, false);
        }
        if (applications.Find(item.Id) is not { } description)
        {
            return (Ceiling.NoAction, CeilingReason.NoDescription, false);
        }
        if (!runners.Any(runner => runner.Accepts(TaskKind.ValidatePullRequest, repository)))
        {
            return (Ceiling.NoAction, CeilingReason.NoRunner, false);
        }

        // From here the problem can be qualified; each next level has a reason when it is not reached
        if (description.Qualify is QualifyMode.Unknown or QualifyMode.None)
        {
            return (Ceiling.Qualification, CeilingReason.QualificationNotDescribed, false);
        }
        var merge = item.Plan.FirstOrDefault(step => step.Kind == StepKind.MergePullRequests && step.EnvironmentId is null);
        if (merge is not { Available: true, Granted: true })
        {
            return (Ceiling.Qualification, CeilingReason.NotGranted, false);
        }
        if (!runners.Any(runner => runner.Accepts(TaskKind.MergePullRequest, repository)))
        {
            return (Ceiling.Qualification, CeilingReason.NoRunner, false);
        }
        if (pullRequest.Checks == ChecksState.Failing)
        {
            return (Ceiling.Qualification, CeilingReason.ChecksFailing, false);
        }
        if (pullRequest.Checks == ChecksState.NotRun)
        {
            return (Ceiling.Qualification, CeilingReason.ChecksNotRun, false);
        }
        if (pullRequest.Checks != ChecksState.Passing)
        {
            return (Ceiling.Qualification, CeilingReason.ChecksNotPassing, false);
        }

        if (pullRequest.Merge != MergeState.Clean)
        {
            return (Ceiling.Qualification, CeilingReason.CannotBeMergedNow, false);
        }

        var kind = pullRequest.Changes.Count == 1 ? pullRequest.Changes[0].Kind : UpdateKind.Unknown;
        if (!description.CanRollBack)
        {
            return (Ceiling.MergeIntoDevelop, CeilingReason.NoRollbackDescribed, false);
        }
        if (!description.HasCheckAfterDeploy)
        {
            return (Ceiling.MergeIntoDevelop, CeilingReason.NoCheckAfterDeployDescribed, false);
        }
        return description.PromotesAutomatically.Contains(kind)
            ? (Ceiling.Production, CeilingReason.None, true)
            : (Ceiling.MergeIntoDevelop, CeilingReason.UpdateClassStopsBeforeProduction, false);
    }

    // What has no action yet (an alert, a secret, a failing build, an image): shown, with "no action defined" (SPEC Q-30)
    private static void AddOtherProblems(List<ProblemRow> rows, WorkItem item, RelevancePolicy policy, InfrastructureIndex? infrastructure, bool dormant = false)
    {
        var application = item.RepositoryFullName ?? item.Title;
        var imageFindings = item.Findings.Where(finding => finding.Image is not null).GroupBy(finding => finding.Kind).ToDictionary(group => group.Key, group => group.Count());
        foreach (var finding in item.Findings)
        {
            ProblemKind? kind = finding.Kind switch
            {
                FindingKind.DependencyAlerts => ProblemKind.DependencyAlert,
                FindingKind.CodeScanningAlerts => ProblemKind.CodeScanningAlert,
                FindingKind.SecretScanningAlerts => ProblemKind.ExposedSecret,
                FindingKind.FailingCi => ProblemKind.FailingCi,
                FindingKind.OutdatedImage => ProblemKind.ImageUpdate,
                FindingKind.FloatingImage => ProblemKind.FloatingImage,
                FindingKind.DependencyScanUnavailable => ProblemKind.DependencyScanUnavailable,
                _ => null,
            };
            // The red build of a dormant repository is part of its own row
            if (kind is null || (dormant && kind == ProblemKind.FailingCi))
            {
                continue;
            }
            var description = finding.Image switch
            {
                { Latest: null } floating => floating.Reference,
                { } image => $"{image.Reference} → {image.Latest}",
                _ => finding.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            var verdict = ImageCode.Of(item, finding, WhyNoAction(item, finding, policy), infrastructure);
            // A repository that builds several images has a finding for each: the row of each has its own places and its own change
            var id = finding.Image is { } update && imageFindings[finding.Kind] > 1 ? $"finding:{item.Id}:{finding.Kind}:{update.Reference}" : $"finding:{item.Id}:{finding.Kind}";
            rows.Add(Of(item, id, kind.Value, description, finding.Severity, finding.Count, Ceiling.NoAction, verdict.Why, application) with { Locations = verdict.Locations });
        }
    }

    // Why nothing is defined for a finding: for an image, what makes it a matter of the platform or of planning rather than a tag to change
    private static CeilingReason WhyNoAction(WorkItem item, Finding finding, RelevancePolicy policy)
    {
        if (finding.Kind != FindingKind.OutdatedImage)
        {
            return CeilingReason.NoActionDefined;
        }
        if (item.Placements.Count > 0 && item.Placements.All(placement => placement.Namespace is { } space && policy.PlatformNamespaces.Contains(space, StringComparer.Ordinal)))
        {
            return CeilingReason.ManagedByPlatform;
        }
        return finding.Image?.Change == Orchestrarr.Core.Fleet.ImageChange.Major ? CeilingReason.MajorNeedsPlan : CeilingReason.NoActionDefined;
    }

    private static ProblemRow Of(WorkItem item, string id, ProblemKind kind, string description, Severity severity, int count, Ceiling ceiling, CeilingReason why, string? application = null) =>
        new(id, item.Id, application ?? item.RepositoryFullName ?? item.Title, kind, description, severity, count, null, null, null, null, null, ceiling, why, false);
}
