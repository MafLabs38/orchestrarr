using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Orchestrarr.Host.SignIn;

/// <summary>Who the session is, whatever it may do: <c>canRead</c> says whether the API will answer, so that a person refused can be told, and can sign out.</summary>
public sealed record SessionDto(string? Name, bool CanRead, bool IsOperator);

internal static class SignInEndpoints
{
    internal const string LogCategory = "Orchestrarr.SignIn";

    public static void MapSignInEndpoints(this WebApplication app, SignInSettings settings)
    {
        // Sends the browser to the identity provider, then back to a page of this application
        app.MapGet(SignInPaths.Login, async (HttpContext context, ILoggerFactory loggers, string? returnUrl) =>
        {
            try
            {
                await context.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties { RedirectUri = ReturnUrl.Safe(returnUrl) });
                return Results.Empty;
            }
            catch (Exception failure) when (IsProviderUnreachable(failure, context.RequestAborted))
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
            return Results.Ok(new SessionDto(ApproverClaims.NameToShow(user), ApproverClaims.CanRead(user, approver, settings), approver?.IsOperator ?? false));
        }).RequireAuthorization(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    }

    // What discovery throws when the provider does not answer: a network error, or an invalid operation that wraps one. Any other fault is a bug, and is not hidden as an outage.
    internal static bool IsProviderUnreachable(Exception failure, CancellationToken requestAborted) => failure switch
    {
        HttpRequestException or IOException => true,
        OperationCanceledException => !requestAborted.IsCancellationRequested,
        InvalidOperationException { InnerException: HttpRequestException or IOException } => true,
        _ => false,
    };
}
