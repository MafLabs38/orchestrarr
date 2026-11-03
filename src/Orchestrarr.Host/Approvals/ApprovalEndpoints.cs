using System.Text.Json;
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

    public static void MapApprovalEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/approvals", (IApprovalStore store) => Results.Ok(store.List().Select(ToDto)));

        // Ids of work items contain a slash, so the work item travels in the body.
        api.MapPost("/approvals", async (ApproveRequest request, HttpContext context, IApproverAccessor accessor, ApprovalService service, CancellationToken ct) =>
        {
            var (approver, refusal) = Authorize(accessor, context);
            if (approver is null)
            {
                return refusal!;
            }

            var result = await service.ApproveAsync(approver, request.WorkItemId ?? string.Empty, request.Steps ?? [], request.PullRequests ?? [], ct);
            return result.Refusal is { } code
                ? Error(JsonCode(code), Statuses.GetValueOrDefault(code, StatusCodes.Status422UnprocessableEntity))
                : Results.Json(new ApproveResponse([.. result.Approvals.Select(ToDto)]), statusCode: StatusCodes.Status201Created);
        });

        api.MapGet("/activity", (IAuditLog audit) =>
        {
            var entries = audit.List();
            return Results.Ok(entries.Select((entry, index) => new ActivityDto(entries.Count - index, entry.At, entry.Event, entry.Subject, entry.OrderId, entry.Detail)));
        });

        api.MapGet("/safety", (IKillSwitchControl killSwitch) => Results.Ok(new SafetyDto(killSwitch.Engaged)));

        // The stop must work even when the trail cannot be written: the trace is attempted, the stop is applied either way.
        api.MapPut("/safety/kill-switch", async (KillSwitchRequest request, HttpContext context, IApproverAccessor accessor, IKillSwitchControl killSwitch, IAuditLog audit, TimeProvider time, CancellationToken ct) =>
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

            try
            {
                killSwitch.Set(request.Engaged);
            }
            catch (KillSwitchUnavailableException)
            {
                // The release could not be saved: the stop stays engaged, and nothing says it was released
                return Error(ApiErrors.KillSwitchUnavailable, StatusCodes.Status503ServiceUnavailable);
            }
            await audit.TryRecordAsync(
                new AuditEntry(time.GetUtcNow(), request.Engaged ? AuditEvents.KillSwitchEngaged : AuditEvents.KillSwitchReleased, approver.Subject, null, null), ct);
            return Results.Ok(new SafetyDto(killSwitch.Engaged));
        });
    }

    public static ApprovalStatus StatusOf(IApproverAccessor accessor, HttpContext context, IKillSwitchControl killSwitch)
    {
        var approver = accessor.Current(context);
        return new(accessor.Mode.ToString().ToLowerInvariant(), accessor.Mode != ApprovalsMode.Disabled, approver?.Subject, accessor.DisplayName(context), approver is { IsPerson: true, IsOperator: true }, killSwitch.Engaged);
    }

    // Who may call the two routes that record something: approving turned on, a local request (the sandbox operator only), somebody signed in.
    private static (Approver? Approver, IResult? Refusal) Authorize(IApproverAccessor accessor, HttpContext context)
    {
        if (accessor.Mode == ApprovalsMode.Disabled)
        {
            return (null, Error(ApiErrors.ApprovalsDisabled, StatusCodes.Status403Forbidden));
        }
        if (accessor.Mode == ApprovalsMode.Development && !SandboxGuard.IsLocalRequest(context))
        {
            return (null, Error(ApiErrors.HostNotAllowed, StatusCodes.Status403Forbidden));
        }
        return accessor.Current(context) is { } approver
            ? (approver, null)
            : (null, Error(ApiErrors.NotSignedIn, StatusCodes.Status401Unauthorized));
    }

    private static IResult Error(string code, int status) => Results.Json(new ErrorResponse(code), statusCode: status);

    // The wire code of a refusal: its name in camelCase, as the interface expects it
    private static string JsonCode(ApprovalRefusal refusal) => JsonNamingPolicy.CamelCase.ConvertName(refusal.ToString());

    private static ApprovalDto ToDto(Approval approval) =>
        new(approval.Id, approval.WorkItemId, approval.Repository, approval.PullRequest, approval.HeadSha, approval.Steps, approval.ApproverSubject, approval.ApprovedAt);
}
