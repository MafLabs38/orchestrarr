using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Orchestrarr.Core.Accounts;

namespace Orchestrarr.Host.Accounts;

/// <summary>
/// Gives the principal of a cookie what its account may do, read from the database at every request (ADR 0018): the cookie only says who it
/// is and which session, so a disabled account, a changed role or a deleted account applies at once. The account was read once already by the
/// validation of the cookie, which leaves it for this request.
/// </summary>
public sealed class AccountClaimsTransformation(IAccountStore accounts, IHttpContextAccessor context) : IClaimsTransformation
{
    /// <summary>Where the validation of the cookie leaves the account it read, for the rest of the request.</summary>
    public const string AccountItem = "orchestrarr:account-object";

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var session = principal.Identities.FirstOrDefault(identity => identity.AuthenticationType == OrchestrarrClaims.Cookie);
        if (session is null)
        {
            return Task.FromResult(principal);
        }

        var account = context.HttpContext?.Items[AccountItem] as Account ?? (principal.AccountId() is { } id ? accounts.Get(id) : null);
        if (account is not { Enabled: true })
        {
            // The cookie rejects such a session before this; a principal without claims is the safe answer if it ever gets here.
            return Task.FromResult(new ClaimsPrincipal(new ClaimsIdentity()));
        }

        return Task.FromResult(new ClaimsPrincipal(OrchestrarrClaims.Identity(account, session.FindFirst(OrchestrarrClaims.Session)?.Value ?? string.Empty)));
    }
}
