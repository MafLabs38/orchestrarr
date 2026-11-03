using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Launch;

/// <summary>
/// Who gives the order, as the identity provider vouched for them. <c>IsPerson</c> is false for a service client: a stolen machine
/// token never produces an order (SPEC D-47). <c>StepUpAt</c> is when they last passed a fresh second factor, if they did.
/// </summary>
public sealed record Approver(string Subject, bool IsPerson, bool IsOperator, DateTimeOffset? StepUpAt);

/// <summary>
/// What the operator approved, before it becomes an order. Whether the environment is production is not said here: the server works it
/// out from the environment description.
/// </summary>
public sealed record OrderRequest(string WorkItemId, string Repository, string HeadSha, string? Environment, IReadOnlyList<StepKind> Steps);
