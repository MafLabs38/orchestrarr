using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Orchestrarr.Host.SignIn;

internal static class SignInEndpoints
{
    public static void MapSignInEndpoints(this WebApplication app)
    {
        // Sends the browser to the identity provider, then back to a page of this application
        app.MapGet("/auth/login", (string? returnUrl) =>
            Results.Challenge(new AuthenticationProperties { RedirectUri = ReturnUrl.Safe(returnUrl) }, [OpenIdConnectDefaults.AuthenticationScheme]));

        // A POST, so that no link or image can sign somebody out; the interface sends it as a form
        app.MapPost("/auth/logout", () =>
            Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));
    }
}
