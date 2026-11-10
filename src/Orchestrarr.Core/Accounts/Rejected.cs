namespace Orchestrarr.Core.Accounts;

/// <summary>Why an operation on accounts was refused: what the API turns into a status and a stable code.</summary>
public enum AccountProblem
{
    Invalid,
    Conflict,
    NotFound,
    Locked,
    Busy,
}

/// <summary>An operation on accounts that cannot be done, with the reason to give to the person who tried.</summary>
public sealed class AccountRejectedException : Exception
{
    public AccountRejectedException(AccountProblem problem, string message, TimeSpan? retryAfter = null)
        : base(message)
    {
        Problem = problem;
        RetryAfter = retryAfter;
    }

    public AccountProblem Problem { get; }

    /// <summary>When the caller may try again, for a refusal that is only for a while.</summary>
    public TimeSpan? RetryAfter { get; }
}
