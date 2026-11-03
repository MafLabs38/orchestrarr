namespace Orchestrarr.Core.Launch;

/// <summary>Why an order was not issued. A code, not a sentence (SPEC D-22).</summary>
public enum IssueRefusal
{
    KillSwitchEngaged,
    NotAPerson,
    NotAnOperator,
    InvalidRequest,
    StepUpRequired,
    SignerUnavailable,
    AuditUnavailable,
}

/// <summary>Why the launcher did not start a run.</summary>
public enum LaunchRefusal
{
    KillSwitchEngaged,
    VerifierUnavailable,
    BadSignature,
    Malformed,
    WrongAudience,
    NotYetValid,
    Expired,
    LifetimeTooLong,
    StepUpRequired,
    OutsideGrantedPowers,
    Replayed,
    AuditUnavailable,
}

public sealed record IssueResult(SignedOrder? Order, IssueRefusal? Refusal)
{
    public bool Issued => Order is not null;
}

/// <summary>The launcher's answer. <c>Order</c> is the verified, normalized order to execute: nothing else is to be run.</summary>
public sealed record LaunchDecision(LaunchRefusal? Refusal, LaunchOrder? Order)
{
    public bool Accepted => Refusal is null;
}
