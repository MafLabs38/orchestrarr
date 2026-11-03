using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Orchestrarr.Host.Approvals;

namespace Orchestrarr.Host.SignIn;

internal static class SignInServiceCollectionExtensions
{
    /// <summary>
    /// The sign-in: a session cookie, opened by the authorization code flow with PKCE against the identity provider, and the person it
    /// gives as the approver. The browser never holds a token. The default scheme is the cookie, so that a request of the API without a
    /// session is a 401 and never a redirect to the provider.
    /// </summary>
    public static IServiceCollection AddSignIn(this IServiceCollection services, SignInSettings settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton<IApproverAccessor>(new OidcApprover(settings));
        if (settings.KeysDirectory is { Length: > 0 } keys)
        {
            services.AddDataProtection().SetApplicationName("orchestrarr").PersistKeysToFileSystem(new DirectoryInfo(keys));
        }

        services
            .AddAuthentication(authentication =>
            {
                authentication.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                authentication.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(cookie => ConfigureCookie(cookie, settings))
            .AddOpenIdConnect(oidc => ConfigureOpenIdConnect(oidc, settings));
        // What the API asks of whoever calls it: a session with an identity the order can carry, and the reader group when there is one
        services.AddAuthorizationBuilder().SetDefaultPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireAssertion(context => CanRead(context.User, settings)).Build());
        return services;
    }

    /// <summary>
    /// Everything the sign-in puts around the application: the check of where a change comes from, the sign-in itself, its routes, and the
    /// rule that every route of the API needs a session. The files of the interface stay public: they say nothing about the fleet.
    /// </summary>
    public static void UseSignIn(this WebApplication app, SignInSettings settings, RouteGroupBuilder api)
    {
        if (settings.KeysDirectory is null && !app.Environment.IsDevelopment())
        {
            app.Logger.LogWarning("SignIn:KeysDirectory is not set: the keys that protect the session cookie are not kept, so a restart signs everybody out and a second replica cannot read the sessions of the first.");
        }
        app.UseMiddleware<SameOriginGuard>(settings);
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapSignInEndpoints();
        api.RequireAuthorization();
    }

    private static bool CanRead(System.Security.Claims.ClaimsPrincipal user, SignInSettings settings) =>
        ApproverClaims.From(user, settings) is { } approver
        && (settings.ReaderGroup is null || approver.IsOperator || ApproverClaims.IsMember(user, settings.GroupsClaim, settings.ReaderGroup));

    private static void ConfigureCookie(CookieAuthenticationOptions cookie, SignInSettings settings)
    {
        cookie.Cookie.Name = settings.SessionCookieName;
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.Cookie.SecurePolicy = Policy(settings);
        cookie.ExpireTimeSpan = settings.SessionLifetime;
        cookie.SlidingExpiration = false;
        // Only the API asks for a session, and it answers with a status, as an API does: nothing is redirected from here
        cookie.Events.OnRedirectToLogin = context => Answer(context, StatusCodes.Status401Unauthorized);
        cookie.Events.OnRedirectToAccessDenied = context => Answer(context, StatusCodes.Status403Forbidden);
    }

    private static Task Answer(RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }

    private static void ConfigureOpenIdConnect(OpenIdConnectOptions oidc, SignInSettings settings)
    {
        oidc.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        oidc.Authority = settings.Authority.AbsoluteUri;
        oidc.ClientId = settings.ClientId;
        oidc.ClientSecret = settings.ClientSecret;
        oidc.ResponseType = OpenIdConnectResponseType.Code;
        oidc.ResponseMode = OpenIdConnectResponseMode.Query;
        oidc.UsePkce = true;
        oidc.CallbackPath = SignInPaths.Callback;
        oidc.SignedOutCallbackPath = SignInPaths.SignedOut;
        // No token is kept: the session holds the claims of the identity token and nothing else
        oidc.SaveTokens = false;
        oidc.GetClaimsFromUserInfoEndpoint = false;
        oidc.MapInboundClaims = false;
        oidc.RequireHttpsMetadata = settings.Authority.Scheme == Uri.UriSchemeHttps;
        oidc.Scope.Clear();
        oidc.Scope.Add("openid");
        oidc.Scope.Add("profile");
        oidc.TokenValidationParameters.NameClaimType = "preferred_username";
        // The handler drops the authentication context by default: it is what says a second factor was passed, so it stays in the session
        oidc.ClaimActions.Remove(Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames.Acr);
        // The browser comes back from the provider with a plain navigation: Lax is enough, and None would need a secure connection
        oidc.CorrelationCookie.SameSite = SameSiteMode.Lax;
        oidc.CorrelationCookie.SecurePolicy = Policy(settings);
        oidc.NonceCookie.SameSite = SameSiteMode.Lax;
        oidc.NonceCookie.SecurePolicy = Policy(settings);

        oidc.Events.OnRedirectToIdentityProvider = context =>
        {
            // Built from the configuration: a forged Host header cannot change where the provider sends the code
            context.ProtocolMessage.RedirectUri = settings.CallbackUrl.AbsoluteUri;
            return Task.CompletedTask;
        };
        oidc.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            context.ProtocolMessage.ClientId = settings.ClientId;
            context.ProtocolMessage.PostLogoutRedirectUri = settings.Origin.AbsoluteUri;
            return Task.CompletedTask;
        };
        // Signing out is the POST of this application. The provider's front-channel logout (a GET that anybody can link to) is not used.
        oidc.Events.OnRemoteSignOut = context =>
        {
            context.SkipHandler();
            return Task.CompletedTask;
        };
        // A sign-in that failed (a refusal of the provider, a code that does not check) goes back to the interface, which says so
        oidc.Events.OnRemoteFailure = context =>
        {
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Orchestrarr.SignIn")
                .LogWarning("Signing in failed: {Failure}", context.Failure?.GetType().Name);
            context.HandleResponse();
            context.Response.Redirect(SignInPaths.Failed);
            return Task.CompletedTask;
        };
    }

    private static CookieSecurePolicy Policy(SignInSettings settings) => settings.Secure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
}
