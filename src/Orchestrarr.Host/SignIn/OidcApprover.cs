using Orchestrarr.Core.Launch;
using Orchestrarr.Host.Approvals;

namespace Orchestrarr.Host.SignIn;

/// <summary>The person signed in through the identity provider (SPEC D-40).</summary>
public sealed class OidcApprover(SignInOptions options) : IApproverAccessor
{
    public ApprovalsMode Mode => ApprovalsMode.OpenIdConnect;

    public Approver? Current(HttpContext context) => ApproverClaims.From(context.User, options);

    public string? DisplayName(HttpContext context) => Current(context) is null ? null : ApproverClaims.DisplayName(context.User);
}
