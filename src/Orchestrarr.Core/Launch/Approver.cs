namespace Orchestrarr.Core.Launch;

/// <summary>
/// Who gives the order, as the identity provider vouched for them. <c>IsPerson</c> is false for a service client: a stolen machine
/// token never produces an order (SPEC D-47).
/// </summary>
public sealed record Approver(string Subject, bool IsPerson, bool IsOperator, bool HasStepUp);

/// <summary>What the operator approved, before it becomes an order.</summary>
public sealed record OrderRequest(
    string WorkItemId,
    string Repository,
    string HeadSha,
    string? Environment,
    bool TouchesProduction,
    IReadOnlyList<Orchestrarr.Core.WorkItems.StepKind> Steps);
