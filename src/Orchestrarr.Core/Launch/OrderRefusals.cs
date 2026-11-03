namespace Orchestrarr.Core.Launch;

/// <summary>Why an order was not issued. A code, not a sentence (SPEC D-22).</summary>
public enum IssueRefusal
{
    KillSwitchEngaged,
    NotAPerson,
    NotAnOperator,
    StepUpRequired,
    NoStepsTicked,
    AuditUnavailable,
}

/// <summary>Why the launcher did not start a run.</summary>
public enum LaunchRefusal
{
    KillSwitchEngaged,
    BadSignature,
    NotYetValid,
    Expired,
    LifetimeTooLong,
    OutsideGrantedPowers,
    Replayed,
    AuditUnavailable,
}

public sealed record IssueResult(SignedOrder? Order, IssueRefusal? Refusal)
{
    public bool Issued => Order is not null;
}

public sealed record LaunchDecision(LaunchRefusal? Refusal)
{
    public bool Accepted => Refusal is null;
}
