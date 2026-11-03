using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Orchestrarr.Host.SignIn;

/// <summary>Who the session is, whatever it may do: <c>canRead</c> says whether the API will answer, so that a person refused can be told, and can sign out.</summary>
public sealed record SessionDto(string? Name, bool CanRead, bool IsOperator);

internal static class SignInEndpoints
{
    internal const string LogCategory = "Orchestrarr.SignIn";

    /// <summary>
    /// A sign-in leaves two cookies until it ends. A page of another site can start many: past this number the oldest are dropped and the
    /// new sign-in goes on, so that the headers of the next requests never grow until the proxy refuses them (and the global stop with
    /// them) and the person is never locked out of signing in.
    /// </summary>
    internal const int MaxSignInsInFlight = 8;

    public static void MapSignInEndpoints(this WebApplication app, SignInSettings settings)
    {
        // Sends the browser to the identity provider, then back to a page of this application
        app.MapGet(SignInPaths.Login, async (HttpContext context, IOptionsMonitor<OpenIdConnectOptions> options, ILoggerFactory loggers, string? returnUrl) =>
        {
            DropOldSignIns(context, options.Get(OpenIdConnectDefaults.AuthenticationScheme), settings);
            try
            {
                await context.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties { RedirectUri = ReturnUrl.Safe(returnUrl) });
                return Results.Empty;
            }
            catch (Exception failure) when (IsProviderUnreachable(failure, context))
            {
                // The provider cannot be reached (its address, its discovery document): the person is told, the detail stays in the log
                loggers.CreateLogger(LogCategory).LogWarning(failure, "The identity provider could not be reached");
                return Results.Redirect(SignInPaths.Failed);
            }
        });

        // A POST, so that no link or image can sign somebody out; the interface sends it as a form
        app.MapPost(SignInPaths.Logout, () =>
            Results.SignOut(new AuthenticationProperties { RedirectUri = SignInPaths.Home }, [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));

        // Open to any session, even one that may not use the API: it is how the interface tells a person that, and offers them the way out
        app.MapGet(SignInPaths.Me, (ClaimsPrincipal user) =>
        {
            var approver = ApproverClaims.From(user, settings);
            return Results.Ok(new SessionDto(ApproverClaims.DisplayName(user) ?? user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, ApproverClaims.CanRead(user, settings), approver?.IsOperator ?? false));
        }).RequireAuthorization(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    }

    // What discovery throws when the provider does not answer: a network error, or an invalid operation that wraps one. Any other fault is a bug, and is not hidden as an outage.
    private static bool IsProviderUnreachable(Exception failure, HttpContext context) => failure switch
    {
        HttpRequestException or IOException => true,
        OperationCanceledException => !context.RequestAborted.IsCancellationRequested,
        InvalidOperationException { InnerException: HttpRequestException or IOException } => true,
        _ => false,
    };

    // The cookies of the sign-ins that were started and never ended, once there are too many: the person always can start a new one
    private static void DropOldSignIns(HttpContext context, OpenIdConnectOptions options, SignInSettings settings)
    {
        var inFlight = context.Request.Cookies.Keys.Where(name => name.StartsWith(options.CorrelationCookie.Name!, StringComparison.Ordinal)).ToList();
        if (inFlight.Count < MaxSignInsInFlight)
        {
            return;
        }
        foreach (var name in inFlight.Concat(context.Request.Cookies.Keys.Where(name => name.StartsWith(options.NonceCookie.Name!, StringComparison.Ordinal))))
        {
            // Secure follows the public address, as it does for the cookies being dropped: over a clear-text loopback (a Development machine) a secure cookie would not be taken
#pragma warning disable S2092 // The flag is set from the configuration: always on for any address that is not a loopback in Development
            context.Response.Cookies.Delete(name, new CookieOptions { Path = SignInPaths.Callback, Secure = settings.Secure, HttpOnly = true, SameSite = SameSiteMode.Lax });
#pragma warning restore S2092
        }
    }
}
