using Orchestrarr.Core.Launch;

namespace Orchestrarr.Host.Approvals;

/// <summary>Who is giving the order, as the host knows them. Null: nobody is signed in. The sign-in through the identity provider replaces the sandbox one.</summary>
public interface IApproverAccessor
{
    ApprovalsMode Mode { get; }

    Approver? Current(HttpContext context);

    /// <summary>A name to show for the approver, never to decide on.</summary>
    string? DisplayName(HttpContext context);
}
