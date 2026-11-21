using Orchestrarr.Core.Applications;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Infrastructure;
using Orchestrarr.Core.Missions;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Core.WorkItems;
using Orchestrarr.Host.Approvals;
using Orchestrarr.Host.Decisions;
using Orchestrarr.Host.Live;
using Orchestrarr.Host.Tasks;

namespace Orchestrarr.Host.Missions;

/// <summary>The rows the maintainer ticked, with the commit each one was seen at: what was seen is what is validated (the table is computed again here).</summary>
public sealed record ValidatePlanRequest(List<ValidatedRow>? Rows);

public sealed record ValidatedRow(string? Id, string? HeadSha);

/// <summary>What became of one ticked row: the problem it started and its first mission, or why nothing started.</summary>
public sealed record ValidatedProblem(string RowId, string? ProblemId, string? MissionId, string? World, string? Refusal);

public sealed record PlanStatus(bool Available);

public sealed record ValidatePlanResponse(IReadOnlyList<ValidatedProblem> Problems);

/// <summary>
/// The validation of the plan (FUNCTIONAL 5.2): the maintainer ticks problems of the table, and each one becomes a problem of the conductor, with its approval and the mandate of its first world. The table is built here again from what
/// was read last, so that nothing the client says can widen what a row may do. **Sandbox only for now**: without <c>Missions:Sandbox</c> there is no approval source that can give a certificate, and the route says so.
/// </summary>
internal static class PlanEndpoints
{
    // What a ticked row asks for: the plan goes as far as the last world, and the standing grant of the application decides how far it really goes (the intersection, read at each crossing)
    private static readonly World PlanCeiling = World.Release;
    private static readonly MissionBudget SandboxBudget = new(30, 3, 50);

    public static void MapPlanEndpoints(this RouteGroupBuilder api)
    {
        // Whether the validation goes through the missions here: the screen sends the selection to the plan when it does, and says so
        api.MapGet("/plan", (IServiceProvider services) => Results.Ok(new PlanStatus(services.GetService<SandboxApprovals>() is not null))).RequireAuthorization(Permissions.FleetRead);
        api.MapPost("/plan/validate", async (ValidatePlanRequest request, HttpContext context, IApproverAccessor accessor, WorkItemCatalog catalog, IApplicationCatalog applications, RunnerRegistry runners, IBatchQueue queue,
            IProblemHistory history, IApprovalStore approvalStore, TimeProvider time, RelevancePolicy policy, InfrastructureCatalog infrastructure, ProblemConductor conductor, IServiceProvider services,
            ILiveNotifier live, CancellationToken ct) =>
        {
            var (approver, refusal) = ApprovalEndpoints.Authorize(accessor, context);
            if (approver is null)
            {
                return refusal!;
            }
            if (services.GetService<SandboxApprovals>() is not { } approvals)
            {
                return Results.Json(new ErrorResponse("planNotAvailable"), statusCode: StatusCodes.Status501NotImplemented);
            }
            var report = await catalog.GetAsync(refresh: false, ct);
            var table = ProblemsEndpoints.VisibleTable(TableInputs.Last(report, infrastructure), applications, runners, queue, history, approvalStore, time, policy).ToDictionary(row => row.Id, StringComparer.Ordinal);
            var started = new List<ValidatedProblem>();
            foreach (var chosen in request.Rows ?? [])
            {
                started.Add(await StartAsync(chosen, table, approver.Subject, approvals, conductor, time, ct));
            }
            live.Publish(LiveTopics.Missions);
            return Results.Json(new ValidatePlanResponse(started), statusCode: StatusCodes.Status201Created);
        }).RequireAuthorization(Permissions.ApprovalsCreate);
    }

    private static async Task<ValidatedProblem> StartAsync(ValidatedRow chosen, Dictionary<string, ProblemRow> table, string approver, SandboxApprovals approvals, ProblemConductor conductor, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var id = chosen.Id ?? string.Empty;
        if (!table.TryGetValue(id, out var row))
        {
            return new ValidatedProblem(id, null, null, null, "theTableChanged");
        }
        if (row.HeadSha is not null && !string.Equals(row.HeadSha, chosen.HeadSha, StringComparison.Ordinal))
        {
            return new ValidatedProblem(id, null, null, null, "thePullRequestMoved");
        }
        var entry = PlanEntryFactory.From(row, approver, time.GetUtcNow(), PlanCeiling, SandboxBudget);
        if (entry is null)
        {
            return new ValidatedProblem(id, null, null, null, "noRepository");
        }
        if (!await approvals.PrepareAsync(entry, cancellationToken))
        {
            return new ValidatedProblem(id, entry.ProblemId, null, null, "noApproval");
        }
        // The staging of the test bed is declared, and its baseline is the snapshot of the lab VMs (a reset brings them back to it)
        var result = await conductor.StartAsync(entry, StagingDeclaration.DeclaredStaging, StagingRequirement.Dependencies, StagingState.BaselineProven, cancellationToken);
        return result.Ok
            ? new ValidatedProblem(id, entry.ProblemId, result.Mission?.Id, result.Mission?.World.ToString(), null)
            : new ValidatedProblem(id, entry.ProblemId, null, null, result.Refusal ?? "refused");
    }
}
