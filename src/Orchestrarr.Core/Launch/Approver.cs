namespace Orchestrarr.Core.Launch;

/// <summary>
/// Who gives the order, as their account says (ADR 0018). <c>IsPerson</c> is false for a service client: a stolen machine
/// token never produces an order (SPEC D-47). <c>StepUpAt</c> is when they last passed a fresh second factor, if they did.
/// </summary>
public sealed record Approver(string Subject, bool IsPerson, bool IsOperator, DateTimeOffset? StepUpAt);
