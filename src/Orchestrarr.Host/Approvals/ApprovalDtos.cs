using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Host.Approvals;

/// <summary>
/// What the client sends to approve: the work item, the steps it ticked and the pull requests (number and commit) it displayed. The
/// server decides the rest, and refuses when what was displayed is not what is there now.
/// </summary>
public sealed record ApproveRequest(string WorkItemId, IReadOnlyList<StepKind> Steps, IReadOnlyList<SeenPullRequest> PullRequests, string? SecondFactorCode = null);

public sealed record ApprovalDto(Guid Id, string WorkItemId, string Repository, int PullRequest, string HeadSha, IReadOnlyList<StepKind> Steps, string Approver, DateTimeOffset ApprovedAt, ApprovalState State, Guid? BatchId, DateTimeOffset? EndedAt);

public sealed record ApproveResponse(IReadOnlyList<ApprovalDto> Approvals);

public sealed record ErrorResponse(string Error);

/// <summary>One entry of the trail. <c>Id</c> is its number in the trail, counted from 1 in the order it was recorded: it never changes.</summary>
public sealed record ActivityDto(long Id, DateTimeOffset At, string Event, string Subject, string? Reference, string? Detail);

public sealed record KillSwitchRequest(bool Engaged);

/// <summary>The global stop: engaged or not, and whether an engagement is saved (false: held only in memory, it would not survive a restart).</summary>
public sealed record SafetyDto(bool KillSwitchEngaged, bool Saved);

/// <summary>
/// What <c>GET /api/v1/system/status</c> says about approving: who is signed in (the identifier that counts, and the name to show), whether
/// they are an operator (the interface offers approving and the stop to nobody else), and the global stop (engaged, and whether it is saved).
/// </summary>
public sealed record ApprovalStatus(string? Operator, string? OperatorName, bool IsOperator, bool KillSwitchEngaged, bool KillSwitchSaved, bool RequiresSecondFactor);
