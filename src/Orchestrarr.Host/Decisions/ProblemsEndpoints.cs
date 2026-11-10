using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Applications;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.WorkItems;
using Orchestrarr.Host.Approvals;
using Orchestrarr.Host.Tasks;

namespace Orchestrarr.Host.Decisions;

/// <summary>The table as the screen reads it: one row per problem, how far each can go, and the version of the descriptions it was computed from.</summary>
public sealed record ProblemsReport(DateTimeOffset? ReadAt, string SourceKind, bool Stale, SourceError? Error, string DescriptionsVersion, IReadOnlyList<ProblemRow> Rows);

public sealed record SelectedRow(string? Id, string? HeadSha, bool StopBeforeProduction);

public sealed record ApproveProblemsRequest(List<SelectedRow>? Rows, string? SecondFactorCode);

/// <summary>
/// The table of decisions (ADR 0019). The client sends which rows it selected and at which commit; <b>the plan is computed here again</b>, from the
/// current table, so that nothing the client says can widen what a row may do.
/// </summary>
internal static class ProblemsEndpoints
{
    public static void MapProblemsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/problems", async (WorkItemCatalog catalog, IApplicationCatalog applications, ExecutorRegistry executors, bool? refresh, CancellationToken ct) =>
        {
            var report = await catalog.GetAsync(refresh ?? false, ct);
            return Results.Ok(new ProblemsReport(report.ReadAt, report.SourceKind, report.Stale, report.Error, applications.Version, DecisionTable.Build(report.Items, applications, Acceptance(executors))));
        }).RequireAuthorization(Permissions.FleetRead);

        api.MapPost("/problems/approve", async (ApproveProblemsRequest request, HttpContext context, IApproverAccessor accessor, WorkItemCatalog catalog, IApplicationCatalog applications, ExecutorRegistry executors, ApprovalService service, CancellationToken ct) =>
        {
            var (approver, refusal) = ApprovalEndpoints.Authorize(accessor, context);
            if (approver is null)
            {
                return refusal!;
            }

            var report = await catalog.GetAsync(refresh: false, ct);
            var table = DecisionTable.Build(report.Items, applications, Acceptance(executors)).ToDictionary(row => row.Id, StringComparer.Ordinal);
            var selection = new List<SelectedProblem>();
            foreach (var chosen in request.Rows ?? [])
            {
                // A row that is gone, moved to another commit, or without an action is not what the operator saw: they read the table again
                if (chosen.Id is null || !table.TryGetValue(chosen.Id, out var row) || row.PullRequest is null || row.GoesUpTo == Ceiling.NoAction
                    || !string.Equals(row.HeadSha, chosen.HeadSha, StringComparison.Ordinal))
                {
                    return ApprovalEndpoints.RefusalResult(ApprovalRefusal.PlanChanged);
                }
                var stops = chosen.StopBeforeProduction && row.CanStopBeforeProduction;
                selection.Add(new SelectedProblem(row.WorkItemId, row.PullRequest.Value, row.HeadSha!, [.. DecisionTable.StepsOf(row.GoesUpTo)],
                    $"goesUpTo={row.GoesUpTo} stopBeforeProduction={stops} descriptions={(applications.Version.Length == 0 ? "none" : applications.Version)}"));
            }

            var result = await service.ApproveSelectionAsync(approver, selection, request.SecondFactorCode, ct);
            return result.Refusal is { } code
                ? ApprovalEndpoints.RefusalResult(code)
                : Results.Json(new ApproveResponse([.. result.Approvals.Select(ApprovalEndpoints.ToDto)]), statusCode: StatusCodes.Status201Created);
        }).RequireAuthorization(Permissions.ApprovalsCreate).RequireRateLimiting(ApprovalsServiceCollectionExtensions.ApprovingPolicy);
    }

    // Only the executors that asked for work lately: one that is gone promises nothing
    private static List<ExecutorAcceptance> Acceptance(ExecutorRegistry executors) =>
        [.. executors.Snapshot().Where(executor => executor.Connected).Select(executor => new ExecutorAcceptance(executor.Kinds, executor.Repositories))];
}
