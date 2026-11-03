using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Host.Approvals;

/// <summary>What the client sends to approve: the work item and the steps it ticked. Nothing else: the server decides the rest.</summary>
public sealed record ApproveRequest(string WorkItemId, IReadOnlyList<StepKind> Steps);

public sealed record ApprovalDto(Guid Id, string WorkItemId, string Repository, int PullRequest, string HeadSha, IReadOnlyList<StepKind> Steps, string Approver, DateTimeOffset ApprovedAt);

public sealed record ApproveResponse(IReadOnlyList<ApprovalDto> Approvals);

public sealed record ErrorResponse(string Error);

public sealed record ActivityDto(DateTimeOffset At, string Event, string Subject, string? Reference, string? Detail);

public sealed record KillSwitchRequest(bool Engaged);

public sealed record SafetyDto(bool KillSwitchEngaged);

/// <summary>What <c>GET /api/v1/system/status</c> says about approving: the mode, who the operator is, the global stop.</summary>
public sealed record ApprovalStatus(string Mode, bool Enabled, string? Operator, bool KillSwitchEngaged);

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
            if (accessor.Mode == ApprovalsMode.Disabled)
            {
                return Results.Json(new ErrorResponse("approvalsDisabled"), statusCode: StatusCodes.Status403Forbidden);
            }
            if (accessor.Current(context) is not { } approver)
            {
                return Results.Json(new ErrorResponse("notSignedIn"), statusCode: StatusCodes.Status401Unauthorized);
            }

            var result = await service.ApproveAsync(approver, request.WorkItemId ?? string.Empty, request.Steps ?? [], ct);
            return result.Refusal is { } refusal
                ? Results.Json(new ErrorResponse(JsonCode(refusal)), statusCode: Statuses.GetValueOrDefault(refusal, StatusCodes.Status422UnprocessableEntity))
                : Results.Json(new ApproveResponse([.. result.Approvals.Select(ToDto)]), statusCode: StatusCodes.Status201Created);
        });

        api.MapGet("/activity", (InMemoryAuditLog audit) =>
            Results.Ok(audit.List().Select(entry => new ActivityDto(entry.At, entry.Event, entry.Subject, entry.OrderId, entry.Detail))));

        api.MapGet("/safety", (InMemoryKillSwitch killSwitch) => Results.Ok(new SafetyDto(killSwitch.Engaged)));

        // The stop must work even when the trail cannot be written: the trace is attempted, the stop is applied either way.
        api.MapPut("/safety/kill-switch", async (KillSwitchRequest request, HttpContext context, IApproverAccessor accessor, InMemoryKillSwitch killSwitch, InMemoryAuditLog audit, TimeProvider time, CancellationToken ct) =>
        {
            if (accessor.Mode == ApprovalsMode.Disabled)
            {
                return Results.Json(new ErrorResponse("approvalsDisabled"), statusCode: StatusCodes.Status403Forbidden);
            }
            if (accessor.Current(context) is not { } approver)
            {
                return Results.Json(new ErrorResponse("notSignedIn"), statusCode: StatusCodes.Status401Unauthorized);
            }
            if (!approver.IsPerson || !approver.IsOperator)
            {
                return Results.Json(new ErrorResponse("notAnOperator"), statusCode: StatusCodes.Status403Forbidden);
            }

            killSwitch.Set(request.Engaged);
            await audit.TryRecordAsync(
                new AuditEntry(time.GetUtcNow(), request.Engaged ? ApprovalAuditEvents.KillSwitchEngaged : ApprovalAuditEvents.KillSwitchReleased, approver.Subject, null, null), ct);
            return Results.Ok(new SafetyDto(killSwitch.Engaged));
        });
    }

    public static ApprovalStatus StatusOf(IApproverAccessor accessor, HttpContext context, InMemoryKillSwitch killSwitch) =>
        new(accessor.Mode.ToString().ToLowerInvariant(), accessor.Mode != ApprovalsMode.Disabled, accessor.Current(context)?.Subject, killSwitch.Engaged);

    private static string JsonCode(ApprovalRefusal refusal)
    {
        var name = refusal.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static ApprovalDto ToDto(Approval approval) =>
        new(approval.Id, approval.WorkItemId, approval.Repository, approval.PullRequest, approval.HeadSha, approval.Steps, approval.ApproverSubject, approval.ApprovedAt);
}
