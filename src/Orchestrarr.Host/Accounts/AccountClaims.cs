using System.Security.Claims;
using Orchestrarr.Core.Accounts;

namespace Orchestrarr.Host.Accounts;

/// <summary>The claims of a signed-in person, and the names of the schemes.</summary>
public static class OrchestrarrClaims
{
    public const string Cookie = "orchestrarr.cookie";
    public const string Permission = "orchestrarr:permission";
    public const string Session = "orchestrarr:session";
    public const string Role = "orchestrarr:role";

    /// <summary>The identity of an account, with what its role gives it now.</summary>
    public static ClaimsIdentity Identity(Account account, string sessionId)
    {
        var identity = new ClaimsIdentity(Cookie, ClaimTypes.Name, Role);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, account.Subject));
        identity.AddClaim(new Claim(ClaimTypes.Name, account.Name));
        identity.AddClaim(new Claim(Role, account.Role));
        identity.AddClaim(new Claim(Session, sessionId));
        foreach (var permission in account.Permissions)
        {
            identity.AddClaim(new Claim(Permission, permission));
        }

        return identity;
    }

    /// <summary>The id of the account a principal speaks for, or null.</summary>
    public static Guid? AccountId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    public static bool Can(this ClaimsPrincipal principal, string permission) => principal.HasClaim(Permission, permission);
}
