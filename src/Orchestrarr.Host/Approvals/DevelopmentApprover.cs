using Orchestrarr.Core.Launch;

namespace Orchestrarr.Host.Approvals;

/// <summary>The sandbox operator: a person with the operator role and a fresh second factor, with no sign-in. Never in production.</summary>
public sealed class DevelopmentApprover(ApprovalsOptions options, TimeProvider time) : IApproverAccessor
{
    public ApprovalsMode Mode => ApprovalsMode.Development;

    public Approver? Current(HttpContext context) => new(options.DevelopmentUser, IsPerson: true, IsOperator: true, StepUpAt: time.GetUtcNow());
}
