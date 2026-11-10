using Orchestrarr.Core.Launch;

namespace Orchestrarr.Host.Approvals;

/// <summary>Who is giving the order, as the host knows them: the person signed in with a local account (ADR 0018). Null: nobody is signed in.</summary>
public interface IApproverAccessor
{
    Approver? Current(HttpContext context);

    /// <summary>A name to show for the approver, never to decide on.</summary>
    string? DisplayName(HttpContext context);
}
