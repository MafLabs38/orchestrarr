using System.Security.Claims;
using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Launch;
using Orchestrarr.Host.Approvals;

namespace Orchestrarr.Host.Accounts;

/// <summary>
/// The person who signed in, as the approvals know them: their subject is the id of their account, they are an operator when their role gives
/// the permission to approve. The proof of the second factor is not here: the runner checks the code.
/// </summary>
public sealed class SessionApprover : IApproverAccessor
{
    public Approver? Current(HttpContext context) =>
        context.User is { Identity.IsAuthenticated: true } user && user.FindFirst(ClaimTypes.NameIdentifier)?.Value is { } subject
            ? new Approver(subject, IsPerson: true, IsOperator: user.Can(Permissions.ApprovalsCreate), StepUpAt: null)
            : null;

    public string? DisplayName(HttpContext context) => context.User.Identity?.IsAuthenticated == true ? context.User.FindFirst(ClaimTypes.Name)?.Value : null;
}
