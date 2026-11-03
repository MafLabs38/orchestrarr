using Orchestrarr.Core.Launch;

namespace Orchestrarr.Host.Approvals;

/// <summary>Nobody can approve.</summary>
public sealed class NoApprover : IApproverAccessor
{
    public ApprovalsMode Mode => ApprovalsMode.Disabled;

    public Approver? Current(HttpContext context) => null;
}
