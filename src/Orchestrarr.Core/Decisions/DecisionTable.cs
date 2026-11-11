using Orchestrarr.Core.Applications;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Decisions;

/// <summary>How far a problem can go by itself (ADR 0019): the lowest of what the description allows, what the executors accept and the state of the problem.</summary>
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
    NoExecutor,
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
    WorkshopVerdict Workshop = WorkshopVerdict.Unknown);

/// <summary>What a connected executor accepts, as it reported it. The table never promises more than they would run: with none connected, nothing can be run, and every row says so.</summary>
public sealed record ExecutorAcceptance(IReadOnlyList<TaskKind> Kinds, IReadOnlyList<string> Repositories)
{
    public bool Accepts(TaskKind kind, string repository) => Kinds.Contains(kind) && Repositories.Any(pattern => RepositoryPattern.Matches(pattern, repository));
}

/// <summary>Builds the rows of the table from the work items, the descriptions and the executors. A pure function: the same input gives the same table.</summary>
public static class DecisionTable
{
    public static IReadOnlyList<ProblemRow> Build(IReadOnlyList<WorkItem> items, IApplicationCatalog applications, IReadOnlyList<ExecutorAcceptance> executors, IReadOnlyList<TaskResultRow>? results = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(applications);
        ArgumentNullException.ThrowIfNull(executors);
        var rows = new List<ProblemRow>();
        foreach (var item in items)
        {
            AddDependencyUpdates(rows, item, applications, executors, results ?? []);
            AddOtherProblems(rows, item);
        }
        return rows;
    }

    // The steps a ceiling stands for: the executor validates, and merges from the merge ceiling up. Production is a decision recorded with the approval.
    public static IReadOnlyList<StepKind> StepsOf(Ceiling ceiling) => ceiling switch
    {
        Ceiling.Qualification => [StepKind.Validate],
        Ceiling.MergeIntoDevelop or Ceiling.Production => [StepKind.Validate, StepKind.MergePullRequests],
        Ceiling.Repair => [StepKind.Repair],
        _ => [],
    };

    private static void AddDependencyUpdates(List<ProblemRow> rows, WorkItem item, IApplicationCatalog applications, IReadOnlyList<ExecutorAcceptance> executors, IReadOnlyList<TaskResultRow> results)
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
            var (ceiling, why, canStop) = CeilingOf(item, pullRequest, applications, executors);
            var mine = results.Where(row => string.Equals(row.Repository, item.RepositoryFullName, StringComparison.OrdinalIgnoreCase) && row.PullRequest == pullRequest.Number && string.Equals(row.HeadSha, pullRequest.HeadSha, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(row => row.At).ToList();
            (ceiling, why, canStop) = WorkshopWord(item, pullRequest, ceiling, why, canStop, executors, mine);
            var change = pullRequest.Changes.Count == 1 ? pullRequest.Changes[0] : null;
            var description = change is { From: not null, To: not null } ? $"{change.Package} {change.From} → {change.To}" : pullRequest.Title;
            rows.Add(new ProblemRow(
                $"pr:{item.RepositoryFullName}#{pullRequest.Number}", item.Id, item.RepositoryFullName, ProblemKind.DependencyUpdate, description,
                null, null, change?.Kind ?? UpdateKind.Unknown, pullRequest.Checks, pullRequest.Url, pullRequest.Number, pullRequest.HeadSha, ceiling, why, canStop, VerdictOf(mine)));
        }
    }

    // The next thing for a pull request is not always the next level: when it fails, on the forge or in the workshop, it is to repair it. The workshop's
    // latest word on this very commit counts (a new commit starts again): a failure makes the row a repair, a repair that worked leaves a patch to apply.
    private static (Ceiling Ceiling, CeilingReason Why, bool CanStop) WorkshopWord(
        WorkItem item, PullRequestInfo pullRequest, Ceiling ceiling, CeilingReason why, bool canStop, IReadOnlyList<ExecutorAcceptance> executors, IReadOnlyList<TaskResultRow> mine)
    {
        if (ceiling == Ceiling.NoAction)
        {
            return (ceiling, why, canStop);
        }
        var repository = item.RepositoryFullName!;
        var canRepair = item.Plan.FirstOrDefault(step => step.Kind == StepKind.Repair && step.EnvironmentId is null) is { Available: true, Granted: true }
            && executors.Any(executor => executor.Accepts(TaskKind.RepairPullRequest, repository));
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

    private static (Ceiling Ceiling, CeilingReason Why, bool CanStop) CeilingOf(WorkItem item, PullRequestInfo pullRequest, IApplicationCatalog applications, IReadOnlyList<ExecutorAcceptance> executors)
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
        if (!executors.Any(executor => executor.Accepts(TaskKind.ValidatePullRequest, repository)))
        {
            return (Ceiling.NoAction, CeilingReason.NoExecutor, false);
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
        if (!executors.Any(executor => executor.Accepts(TaskKind.MergePullRequest, repository)))
        {
            return (Ceiling.Qualification, CeilingReason.NoExecutor, false);
        }
        if (pullRequest.Checks == ChecksState.Failing)
        {
            return (Ceiling.Qualification, CeilingReason.ChecksFailing, false);
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
    private static void AddOtherProblems(List<ProblemRow> rows, WorkItem item)
    {
        var application = item.RepositoryFullName ?? item.Title;
        foreach (var finding in item.Findings)
        {
            ProblemKind? kind = finding.Kind switch
            {
                FindingKind.DependencyAlerts => ProblemKind.DependencyAlert,
                FindingKind.CodeScanningAlerts => ProblemKind.CodeScanningAlert,
                FindingKind.SecretScanningAlerts => ProblemKind.ExposedSecret,
                FindingKind.FailingCi => ProblemKind.FailingCi,
                FindingKind.OutdatedImage => ProblemKind.ImageUpdate,
                FindingKind.DependencyScanUnavailable => ProblemKind.DependencyScanUnavailable,
                _ => null,
            };
            if (kind is null)
            {
                continue;
            }
            var description = finding.Image is { } image ? $"{image.Reference} → {image.Latest}" : finding.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            rows.Add(Of(item, $"finding:{item.Id}:{finding.Kind}", kind.Value, description, finding.Severity, finding.Count, Ceiling.NoAction, CeilingReason.NoActionDefined, application));
        }
    }

    private static ProblemRow Of(WorkItem item, string id, ProblemKind kind, string description, Severity severity, int count, Ceiling ceiling, CeilingReason why, string? application = null) =>
        new(id, item.Id, application ?? item.RepositoryFullName ?? item.Title, kind, description, severity, count, null, null, null, null, null, ceiling, why, false);
}
