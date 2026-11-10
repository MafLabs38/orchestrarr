using System.Text.Json;
using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Host.Approvals;

internal static class ApprovalEndpoints
{
    // A refusal is a code and a status; the interface words it (SPEC D-22). Nothing about the inside reaches the client.
    private static readonly Dictionary<ApprovalRefusal, int> Statuses = new()
    {
        [ApprovalRefusal.KillSwitchEngaged] = StatusCodes.Status423Locked,
        [ApprovalRefusal.NotAPerson] = StatusCodes.Status403Forbidden,
        [ApprovalRefusal.NotAnOperator] = StatusCodes.Status403Forbidden,
        [ApprovalRefusal.UnknownWorkItem] = StatusCodes.Status404NotFound,
        [ApprovalRefusal.PullRequestsNotRead] = StatusCodes.Status409Conflict,
        [ApprovalRefusal.PlanChanged] = StatusCodes.Status409Conflict,
        [ApprovalRefusal.AlreadyApproved] = StatusCodes.Status409Conflict,
        [ApprovalRefusal.StoreFull] = StatusCodes.Status503ServiceUnavailable,
        [ApprovalRefusal.AuditUnavailable] = StatusCodes.Status503ServiceUnavailable,
    };

    // How many of the newest approvals or entries a list answers by default, and at most
    private const int DefaultPageSize = 200;
    private const int MaxPageSize = 1000;

    public static void MapApprovalEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/approvals", (IApprovalStore store, int? limit) => WhenReadable(() => Results.Ok(store.List(PageSize(limit)).Select(ToDto).ToList()))).RequireAuthorization(Permissions.FleetRead);

        // Ids of work items contain a slash, so the work item travels in the body.
        api.MapPost("/approvals", async (ApproveRequest request, HttpContext context, IApproverAccessor accessor, ApprovalService service, CancellationToken ct) =>
        {
            var (approver, refusal) = Authorize(accessor, context);
            if (approver is null)
            {
                return refusal!;
            }

            var result = await service.ApproveAsync(approver, request.WorkItemId ?? string.Empty, request.Steps ?? [], request.PullRequests ?? [], request.SecondFactorCode, ct);
            return result.Refusal is { } code
                ? Error(JsonCode(code), Statuses.GetValueOrDefault(code, StatusCodes.Status422UnprocessableEntity))
                : Results.Json(new ApproveResponse([.. result.Approvals.Select(ToDto)]), statusCode: StatusCodes.Status201Created);
        }).RequireAuthorization(Permissions.ApprovalsCreate).RequireRateLimiting(ApprovalsServiceCollectionExtensions.ApprovingPolicy);

        // Named "audit", not "activity" (the screen keeps its name): content blockers in browsers refuse a request to a path called "activity", and
        // the screen then shows "Failed to fetch" while the server never sees the request.
        api.MapGet("/audit", (IAuditLog audit, int? limit) => WhenReadable(() =>
            Results.Ok(audit.Page(PageSize(limit)).Select(row => new ActivityDto(row.Id, row.Entry.At, row.Entry.Event, row.Entry.Subject, row.Entry.OrderId, row.Entry.Detail)).ToList()))).RequireAuthorization(Permissions.AuditRead);

        api.MapGet("/safety", (IKillSwitchControl killSwitch) => Results.Ok(SafetyOf(killSwitch))).RequireAuthorization(Permissions.FleetRead);

        // Engaging must work even when the trail cannot be written; a release needs to be saved and traced (the stop does both in one write).
        api.MapPut("/safety/kill-switch", async (KillSwitchRequest request, HttpContext context, IApproverAccessor accessor, IKillSwitchControl killSwitch, TimeProvider time, CancellationToken ct) =>
        {
            var (approver, refusal) = Authorize(accessor, context);
            if (approver is null)
            {
                return refusal!;
            }
            if (!approver.IsPerson || !approver.IsOperator)
            {
                return Error(ApiErrors.NotAnOperator, StatusCodes.Status403Forbidden);
            }

            var entry = new AuditEntry(time.GetUtcNow(), request.Engaged ? AuditEvents.KillSwitchEngaged : AuditEvents.KillSwitchReleased, approver.Subject, null, null);
            return await killSwitch.ChangeAsync(request.Engaged, entry, ct) switch
            {
                // The release could not be saved, or traced: the stop stays engaged, and nothing says it was released
                KillSwitchChange.NotSaved => Error(ApiErrors.KillSwitchUnavailable, StatusCodes.Status503ServiceUnavailable),
                KillSwitchChange.NotTraced => Error(ApiErrors.AuditUnavailable, StatusCodes.Status503ServiceUnavailable),
                _ => Results.Ok(SafetyOf(killSwitch)),
            };
        }).RequireAuthorization(Permissions.StopControl);
    }

    // A list that cannot be read is an error and never an empty list: the answer says the storage is unavailable
    private static IResult WhenReadable(Func<IResult> read)
    {
        try
        {
            return read();
        }
        catch (StorageUnavailableException)
        {
            return Error(ApiErrors.StorageUnavailable, StatusCodes.Status503ServiceUnavailable);
        }
    }

    // The newest ones: a trail in a file can hold hundreds of thousands, and the screens poll
    internal static int PageSize(int? limit) => Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

    // One read of the stop for both answers
    private static SafetyDto SafetyOf(IKillSwitchControl killSwitch)
    {
        var state = killSwitch.State();
        return new SafetyDto(state.Engaged, state.Saved);
    }

    public static ApprovalStatus StatusOf(IApproverAccessor accessor, HttpContext context, IKillSwitchControl killSwitch, bool requiresSecondFactor)
    {
        var approver = accessor.Current(context);
        var state = killSwitch.State();
        return new(approver?.Subject, accessor.DisplayName(context), approver is { IsPerson: true, IsOperator: true }, state.Engaged, state.Saved, requiresSecondFactor);
    }

    // Who may call the two routes that record something: somebody signed in (the permission is checked by the route's policy first).
    private static (Approver? Approver, IResult? Refusal) Authorize(IApproverAccessor accessor, HttpContext context)
    {
        return accessor.Current(context) is { } approver
            ? (approver, null)
            : (null, Error(ApiErrors.NotSignedIn, StatusCodes.Status401Unauthorized));
    }

    private static IResult Error(string code, int status) => Results.Json(new ErrorResponse(code), statusCode: status);

    // The wire code of a refusal: its name in camelCase, as the interface expects it
    private static string JsonCode(ApprovalRefusal refusal) => JsonNamingPolicy.CamelCase.ConvertName(refusal.ToString());

    private static ApprovalDto ToDto(Approval approval) =>
        new(approval.Id, approval.WorkItemId, approval.Repository, approval.PullRequest, approval.HeadSha, approval.Steps, approval.ApproverSubject, approval.ApprovedAt, approval.State, approval.BatchId, approval.EndedAt);
}
