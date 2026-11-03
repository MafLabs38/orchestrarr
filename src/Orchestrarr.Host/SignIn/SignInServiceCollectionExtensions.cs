using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Orchestrarr.Host.SignIn;

internal static class SignInServiceCollectionExtensions
{
    public const string CookieName = "orchestrarr.session";

    /// <summary>
    /// The sign-in: a session cookie, opened by the authorization code flow with PKCE against the identity provider. The browser never holds a
    /// token. The default scheme is the cookie, so that a request of the API without a session is a 401 and never a redirect to the provider.
    /// </summary>
    public static IServiceCollection AddSignIn(this IServiceCollection services, SignInOptions options)
    {
        services.AddSingleton(options);
        if (options.KeysDirectory is { Length: > 0 } keys)
        {
            services.AddDataProtection().SetApplicationName("orchestrarr").PersistKeysToFileSystem(new DirectoryInfo(keys));
        }

        services
            .AddAuthentication(authentication =>
            {
                authentication.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                authentication.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(cookie => ConfigureCookie(cookie, options))
            .AddOpenIdConnect(oidc => ConfigureOpenIdConnect(oidc, options));
        services.AddAuthorization();
        return services;
    }

    private static void ConfigureCookie(CookieAuthenticationOptions cookie, SignInOptions options)
    {
        cookie.Cookie.Name = CookieName;
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.Cookie.SecurePolicy = Policy(options);
        cookie.ExpireTimeSpan = TimeSpan.FromHours(options.SessionHours);
        cookie.SlidingExpiration = false;
        cookie.LoginPath = "/auth/login";
        cookie.ReturnUrlParameter = "returnUrl";
        // The API answers with a status, as an API does; only a page of the interface is sent to the identity provider
        cookie.Events.OnRedirectToLogin = context => Refuse(context, StatusCodes.Status401Unauthorized);
        cookie.Events.OnRedirectToAccessDenied = context => Refuse(context, StatusCodes.Status403Forbidden);
    }

    private static Task Refuse(RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal))
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        }
        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }

    private static void ConfigureOpenIdConnect(OpenIdConnectOptions oidc, SignInOptions options)
    {
        oidc.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        oidc.Authority = options.Authority;
        oidc.ClientId = options.ClientId;
        oidc.ClientSecret = options.ClientSecret;
        oidc.ResponseType = OpenIdConnectResponseType.Code;
        oidc.ResponseMode = OpenIdConnectResponseMode.Query;
        oidc.UsePkce = true;
        oidc.CallbackPath = SignInOptions.CallbackPath;
        oidc.SignedOutCallbackPath = "/auth/signed-out";
        // No token is kept: the session holds the claims of the identity token and nothing else
        oidc.SaveTokens = false;
        oidc.GetClaimsFromUserInfoEndpoint = false;
        oidc.MapInboundClaims = false;
        oidc.RequireHttpsMetadata = options.Authority!.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        oidc.Scope.Clear();
        oidc.Scope.Add("openid");
        oidc.Scope.Add("profile");
        oidc.TokenValidationParameters.NameClaimType = ApproverClaims.PreferredName;
        // The browser comes back from the provider with a plain navigation: Lax is enough, and None would need a secure connection
        oidc.CorrelationCookie.SameSite = SameSiteMode.Lax;
        oidc.CorrelationCookie.SecurePolicy = Policy(options);
        oidc.NonceCookie.SameSite = SameSiteMode.Lax;
        oidc.NonceCookie.SecurePolicy = Policy(options);

        oidc.Events.OnRedirectToIdentityProvider = context =>
        {
            // Built from the configuration: a forged Host header cannot change where the provider sends the code
            context.ProtocolMessage.RedirectUri = options.CallbackUrl.AbsoluteUri;
            return Task.CompletedTask;
        };
        oidc.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            context.ProtocolMessage.ClientId = options.ClientId;
            context.ProtocolMessage.PostLogoutRedirectUri = options.Origin.AbsoluteUri;
            return Task.CompletedTask;
        };
        // A sign-in that failed (a refusal of the provider, a code that does not check) goes back to the interface, which says so. The reason stays in the log.
        oidc.Events.OnRemoteFailure = context =>
        {
            context.HandleResponse();
            context.Response.Redirect(SignInFailedPath);
            return Task.CompletedTask;
        };
    }

    public const string SignInFailedPath = "/?signInFailed=1";

    private static CookieSecurePolicy Policy(SignInOptions options) => options.Secure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
}
