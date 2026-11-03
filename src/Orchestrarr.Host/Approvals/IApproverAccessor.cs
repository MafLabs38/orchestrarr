using Orchestrarr.Core.Launch;

namespace Orchestrarr.Host.Approvals;

/// <summary>Who is giving the order, as the host knows them. Null: nobody is signed in. Two implementations: the sandbox operator (<c>Development</c>, the local sandbox only) and the person signed in through the identity provider (<c>OpenIdConnect</c>); <c>Disabled</c> has none.</summary>
public interface IApproverAccessor
{
    ApprovalsMode Mode { get; }

    Approver? Current(HttpContext context);

    /// <summary>A name to show for the approver, never to decide on.</summary>
    string? DisplayName(HttpContext context);
}
