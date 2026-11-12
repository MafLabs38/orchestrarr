using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Applications;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Infrastructure;
using Orchestrarr.Core.WorkItems;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Host.Approvals;
using Orchestrarr.Host.Tasks;
using Orchestrarr.Storage;

namespace Orchestrarr.Host.Decisions;

/// <summary>The table as the screen reads it: one row per problem, how far each can go, and the version of the descriptions it was computed from.</summary>
public sealed record ProblemsReport(
    DateTimeOffset? ReadAt, string SourceKind, bool Stale, SourceError? Error, string DescriptionsVersion, IReadOnlyList<ProblemRow> Rows, int Skipped = 0, CodeScopeStatus? Code = null);

/// <summary>The state of the code of the scope as the table found it: how many places it reads, what could not be read, and whether it is the last good reading (ADR 0028).</summary>
public sealed record CodeScopeStatus(bool InScope, int Repositories, int Files, DateTimeOffset? ReadAt, bool Stale, IReadOnlyList<RepositoryProblem> Problems);

/// <summary>A change to a file of the scope, computed and not applied: the line before and after, and the unified diff.</summary>
public sealed record CodeChange(string Repository, string Path, int Line, string Before, string After, string Diff);

/// <summary>What changing the version of a row would write: where, with what, and the places that are not ours to change.</summary>
public sealed record CodeChangeReport(string RowId, string? Tag, IReadOnlyList<CodeChange> Changes, IReadOnlyList<CodeLocation> Left);

/// <summary><c>GoesUpTo</c> is how far the row went when the operator saw it: the approval is for that, and for no more.</summary>
public sealed record SelectedRow(string? Id, string? HeadSha, Ceiling? GoesUpTo, bool StopBeforeProduction);

public sealed record ApproveProblemsRequest(List<SelectedRow>? Rows, string? SecondFactorCode, string? DescriptionsVersion);

/// <summary>
/// The table of decisions (ADR 0019). The client sends which rows it selected and at which commit; <b>the plan is computed here again</b>, from the
/// current table, so that nothing the client says can widen what a row may do.
/// </summary>
internal static class ProblemsEndpoints
{
    public static void MapProblemsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/problems", async (WorkItemCatalog catalog, IApplicationCatalog applications, RunnerRegistry runners, IBatchQueue queue, IProblemHistory history, IApprovalStore approvals, TimeProvider time, RelevancePolicy policy, InfrastructureCatalog infrastructure, IK3sReleases k3s, bool? refresh, CancellationToken ct) =>
        {
            var report = await catalog.GetAsync(refresh ?? false, ct);
            var code = await infrastructure.GetAsync(refresh ?? false, ct);
            var all = FullTable(report, applications, runners, queue, time, policy, code.Index, infrastructure.Scope, await k3s.GetAsync(ct));
            var rows = Visible(all, history, approvals);
            return Results.Ok(new ProblemsReport(report.ReadAt, report.SourceKind, report.Stale, report.Error, applications.Version, rows, all.Count - rows.Count,
                new CodeScopeStatus(code.Index.InScope, infrastructure.Scope.Repositories.Count, code.Index.FileCount, code.ReadAt, code.Stale, code.Index.Problems)));
        }).RequireAuthorization(Permissions.FleetRead);

        // The change that would write the proposed version where the code of the scope writes it: shown, never applied (Orchestrarr holds no write power, ADR 0028)
        api.MapGet("/problems/change", async (string? id, WorkItemCatalog catalog, IApplicationCatalog applications, RunnerRegistry runners, IBatchQueue queue, TimeProvider time, RelevancePolicy policy,
            InfrastructureCatalog infrastructure, IK3sReleases k3s, CancellationToken ct) =>
        {
            var report = await catalog.GetAsync(refresh: false, ct);
            var code = await infrastructure.GetAsync(refresh: false, ct);
            var row = FullTable(report, applications, runners, queue, time, policy, code.Index, infrastructure.Scope, await k3s.GetAsync(ct)).FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (row is null)
            {
                return Results.Json(new ErrorResponse("notFound"), statusCode: StatusCodes.Status404NotFound);
            }
            if (row.ProposedTag is not { } tag || row.Locations is not { Count: > 0 } locations)
            {
                return Results.Json(new ErrorResponse("noChange"), statusCode: StatusCodes.Status409Conflict);
            }
            var changes = new List<CodeChange>();
            var left = new List<CodeLocation>();
            foreach (var location in locations)
            {
                var change = code.Index.Content(location.Repository, location.Path) is { } content ? VersionPatch.Replace(content, location, tag) : null;
                if (change is null)
                {
                    left.Add(location);
                    continue;
                }
                changes.Add(new CodeChange(location.Repository, location.Path, location.Line, change.Before, change.After, change.Diff));
            }
            return Results.Ok(new CodeChangeReport(row.Id, tag, changes, left));
        }).RequireAuthorization(Permissions.FleetRead);

        api.MapPost("/problems/approve", async (ApproveProblemsRequest request, HttpContext context, IApproverAccessor accessor, IProblemHistory history, IApprovalStore approvalStore, ILoggerFactory loggers, TimeProvider time, RelevancePolicy policy, WorkItemCatalog catalog, IApplicationCatalog applications, RunnerRegistry runners, IBatchQueue queue, ApprovalService service, CancellationToken ct) =>
        {
            var (approver, refusal) = ApprovalEndpoints.Authorize(accessor, context);
            if (approver is null)
            {
                return refusal!;
            }

            var report = await catalog.GetAsync(refresh: false, ct);
            var table = Visible(DecisionTable.Build(report.Items, applications, Acceptance(runners), WorkshopResults(queue), report.Applications, time.GetUtcNow(), policy), history, approvalStore).ToDictionary(row => row.Id, StringComparer.Ordinal);
            // What the operator saw is what is approved: the rule lives once, in Core
            var resolution = DecisionSelection.Resolve(table, [.. (request.Rows ?? []).Select(row => new ChosenRow(row.Id, row.HeadSha, row.GoesUpTo, row.StopBeforeProduction))], request.DescriptionsVersion, applications.Version);
            if (resolution.Refused)
            {
                await service.TraceRefusalAsync(approver, resolution.Mismatch?.WorkItemId ?? string.Empty, ApprovalRefusal.PlanChanged);
                return ApprovalEndpoints.RefusalResult(ApprovalRefusal.PlanChanged);
            }

            var result = await service.ApproveSelectionAsync(approver, resolution.Selection, request.SecondFactorCode, ct);
            if (result.Refusal is { } code)
            {
                return ApprovalEndpoints.RefusalResult(code);
            }
            RecordApproved(history, loggers.CreateLogger("Orchestrarr.Decisions"), table.Values, result.Approvals, time.GetUtcNow(), context.User.Identity?.Name ?? approver.Subject);
            return Results.Json(new ApproveResponse([.. result.Approvals.Select(ApprovalEndpoints.ToDto)]), statusCode: StatusCodes.Status201Created);
        }).RequireAuthorization(Permissions.ApprovalsCreate).RequireRateLimiting(ApprovalsServiceCollectionExtensions.ApprovingPolicy);

        api.MapProblemHistoryEndpoints();
    }

    // The rows a person decides on: the ones set aside for their present target are left out, and the ones an approval is working on are marked. A store that cannot be
    // read hides nothing: the table still shows the problems.
    internal static IReadOnlyList<ProblemRow> Visible(IReadOnlyList<ProblemRow> table, IProblemHistory history, IApprovalStore approvals)
    {
        HashSet<(string, string)> skips;
        HashSet<string> working;
        try
        {
            skips = [.. history.ActiveSkips().Select(skip => (skip.RowId, skip.Target))];
            working = new HashSet<string>(approvals.List(ApprovalsLookedAt).Where(approval => approval.State is ApprovalState.Waiting or ApprovalState.Taken)
                .Select(approval => $"{approval.Repository}#{approval.PullRequest}"), StringComparer.OrdinalIgnoreCase);
        }
        catch (StorageUnavailableException)
        {
            return table;
        }
        // A skip made when the row was less serious lapses as soon as it is a danger: a danger is never hidden
        return [.. table.Where(row => row.Severity == Core.WorkItems.Severity.Danger || !skips.Contains((row.Id, row.Description)))
            .Select(row => row.PullRequest is { } number && working.Contains($"{row.Source ?? row.Application}#{number}") ? row with { InProgress = true } : row)];
    }

    // Every row there is, before the skips are applied
    internal static IReadOnlyList<ProblemRow> FullTable(WorkItemReport report, IApplicationCatalog applications, RunnerRegistry runners, IBatchQueue queue, TimeProvider time, RelevancePolicy policy, InfrastructureIndex? infrastructure = null, DescriptionScope? scope = null, K3sReleases? releases = null)
    {
        var rows = DecisionTable.Build(report.Items, applications, Acceptance(runners), WorkshopResults(queue), report.Applications, time.GetUtcNow(), policy, infrastructure);
        return scope is { Platforms.Count: > 0 } && infrastructure is not null ? [.. rows, .. PlatformRows.Build(scope.Platforms, infrastructure, releases)] : rows;
    }

    internal static IReadOnlyList<ProblemRow> VisibleTable(WorkItemReport report, IApplicationCatalog applications, RunnerRegistry runners, IBatchQueue queue, IProblemHistory history, IApprovalStore approvals, TimeProvider time, RelevancePolicy policy) =>
        Visible(FullTable(report, applications, runners, queue, time, policy), history, approvals);

    // What the operator approved, photographed: the live row is gone once the pull request is merged. A history that cannot be written does not undo the approval.
    private static void RecordApproved(IProblemHistory history, ILogger logger, IEnumerable<ProblemRow> table, IReadOnlyList<Approval> approvals, DateTimeOffset at, string by)
    {
        var rows = table.Where(row => row.PullRequest is not null).ToList();
        foreach (var approval in approvals)
        {
            var row = rows.FirstOrDefault(candidate => candidate.WorkItemId == approval.WorkItemId && candidate.PullRequest == approval.PullRequest);
            if (row is null)
            {
                continue;
            }
            try
            {
                history.Record(new HistoryEntry(0, row.Id, row.Description, HistoryState.Approved, row.Application, row.Kind, row.Description, approval.Repository, approval.PullRequest,
                    approval.HeadSha, approval.Id, approval.BatchId, at, by, null));
            }
            catch (StorageUnavailableException failure)
            {
                // The approval stands; only its photograph is missing, and the trail of the approval says what was decided
                logger.LogWarning(failure, "The history could not record the approval {Approval}; the decision stands without its line", approval.Id);
            }
        }
    }

    // The newest results of the tasks that ended: a commit whose verdict is older than this window shows as never run. Bounded on purpose (the query runs on every
    // read of the table); a per-commit store comes with a fleet large enough to need it.
    private const int WorkshopWindow = 500;

    // How far back an approval is still looked at, to mark a row in progress and to read the outcome of a line of the history
    internal const int ApprovalsLookedAt = 2000;

    // What the workshop said lately about each commit. A store that cannot be read gives none: the table still shows the problems, from the forge alone.
    internal static IReadOnlyList<TaskResultRow> WorkshopResults(IBatchQueue queue)
    {
        try
        {
            return queue.TaskResults(WorkshopWindow);
        }
        catch (StorageUnavailableException)
        {
            return [];
        }
    }

    // Only the runners that asked for work lately: one that is gone promises nothing
    internal static List<RunnerAcceptance> Acceptance(RunnerRegistry runners) =>
        [.. runners.Snapshot().Where(runner => runner.Connected).Select(runner => new RunnerAcceptance(runner.Kinds, runner.Repositories))];
}
