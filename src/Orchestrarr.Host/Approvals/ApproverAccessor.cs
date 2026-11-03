using Orchestrarr.Core.Launch;

namespace Orchestrarr.Host.Approvals;

/// <summary>Who is giving the order, as the host knows them. Null: nobody is signed in. The sign-in through the identity provider replaces the sandbox one.</summary>
public interface IApproverAccessor
{
    ApprovalsMode Mode { get; }

    Approver? Current(HttpContext context);
}

/// <summary>Nobody can approve.</summary>
public sealed class NoApprover : IApproverAccessor
{
    public ApprovalsMode Mode => ApprovalsMode.Disabled;

    public Approver? Current(HttpContext context) => null;
}

/// <summary>The sandbox operator: a person with the operator role and a fresh second factor, with no sign-in. Never in production.</summary>
public sealed class DevelopmentApprover(ApprovalsOptions options, TimeProvider time) : IApproverAccessor
{
    public ApprovalsMode Mode => ApprovalsMode.Development;

    public Approver? Current(HttpContext context) => new(options.DevelopmentUser, IsPerson: true, IsOperator: true, StepUpAt: time.GetUtcNow());
}
