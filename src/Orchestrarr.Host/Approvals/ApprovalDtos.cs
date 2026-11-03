using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Host.Approvals;

/// <summary>
/// What the client sends to approve: the work item, the steps it ticked and the pull requests (number and commit) it displayed. The
/// server decides the rest, and refuses when what was displayed is not what is there now.
/// </summary>
public sealed record ApproveRequest(string WorkItemId, IReadOnlyList<StepKind> Steps, IReadOnlyList<SeenPullRequest> PullRequests);

public sealed record ApprovalDto(Guid Id, string WorkItemId, string Repository, int PullRequest, string HeadSha, IReadOnlyList<StepKind> Steps, string Approver, DateTimeOffset ApprovedAt);

public sealed record ApproveResponse(IReadOnlyList<ApprovalDto> Approvals);

public sealed record ErrorResponse(string Error);

/// <summary>One entry of the trail. <c>Id</c> is its place from the oldest, which does not change as the trail grows.</summary>
public sealed record ActivityDto(int Id, DateTimeOffset At, string Event, string Subject, string? Reference, string? Detail);

public sealed record KillSwitchRequest(bool Engaged);

public sealed record SafetyDto(bool KillSwitchEngaged);

/// <summary>What <c>GET /api/v1/system/status</c> says about approving: the mode, who the operator is, the global stop.</summary>
public sealed record ApprovalStatus(string Mode, bool Enabled, string? Operator, string? OperatorName, bool KillSwitchEngaged);
