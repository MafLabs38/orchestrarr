using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Orchestrarr.Host.Approvals;

namespace Orchestrarr.Host.SignIn;

/// <summary>Who the session is, whatever it may do: <c>canRead</c> says whether the API will answer, so that a person refused can be told, and can sign out.</summary>
public sealed record SessionDto(string? Name, bool CanRead, bool IsOperator);

internal static class SignInEndpoints
{
    // A sign-in leaves two cookies until it ends. A page of another site can start many: past this number the host stops adding more, so that
    // the headers of the next requests do not grow until the proxy refuses them (and the global stop with them).
    private const int MaxSignInsInFlight = 8;
    internal const string LogCategory = "Orchestrarr.SignIn";
    private const string CorrelationCookiePrefix = ".AspNetCore.Correlation.";

    public static void MapSignInEndpoints(this WebApplication app, SignInSettings settings)
    {
        // Sends the browser to the identity provider, then back to a page of this application
        app.MapGet(SignInPaths.Login, async (HttpContext context, ILoggerFactory loggers, string? returnUrl) =>
        {
            if (context.Request.Cookies.Keys.Count(name => name.StartsWith(CorrelationCookiePrefix, StringComparison.Ordinal)) >= MaxSignInsInFlight)
            {
                return Results.Json(new ErrorResponse(ApiErrors.TooManySignIns), statusCode: StatusCodes.Status429TooManyRequests);
            }
            try
            {
                await context.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties { RedirectUri = ReturnUrl.Safe(returnUrl) });
                return Results.Empty;
            }
            catch (Exception failure) when (failure is InvalidOperationException or HttpRequestException or IOException)
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
            Results.Ok(new SessionDto(ApproverClaims.DisplayName(user) ?? user.FindFirst("sub")?.Value, ApproverClaims.CanRead(user, settings), ApproverClaims.From(user, settings)?.IsOperator ?? false)))
            .RequireAuthorization(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    }
}
