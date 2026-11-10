using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Orchestrarr.Core.Accounts;

namespace Orchestrarr.Host.Accounts;

public static class SecurityExtensions
{
    // The sign-in and the accounts take small bodies: a name, a password, a role
    private const long SmallBody = 8 * 1024;

    /// <summary>
    /// Sign-in by cookie for people, one policy per permission, and a fallback policy that requires a sign-in everywhere else: a route
    /// nobody thought about is closed (ADR 0018). There is no mode that trusts the network.
    /// </summary>
    public static IServiceCollection AddOrchestrarrSecurity(this IServiceCollection services, AuthConfig auth, string? defaultKeysFolder)
    {
        var data = services.AddDataProtection().SetApplicationName("Orchestrarr");
        if ((auth.KeysDirectory ?? defaultKeysFolder) is { } keys)
        {
            data.PersistKeysToFileSystem(PrivateFolder(keys));
        }

        services.AddAuthentication(OrchestrarrClaims.Cookie).AddCookie(OrchestrarrClaims.Cookie, cookie => ConfigureCookie(cookie, auth));
        services.AddHttpContextAccessor();
        services.AddSingleton<IClaimsTransformation, AccountClaimsTransformation>();
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            foreach (var permission in Permissions.All)
            {
                options.AddPolicy(permission, policy => policy.RequireAuthenticatedUser().RequireClaim(OrchestrarrClaims.Permission, permission));
            }
        });
        return services;
    }

    public static WebApplication UseOrchestrarrSecurity(this WebApplication app)
    {
        app.Use((context, next) =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/api/v1/auth", StringComparison.Ordinal) || path.StartsWithSegments("/api/v1/accounts", StringComparison.Ordinal))
            {
                context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = SmallBody;
            }

            return next(context);
        });
        app.UseAuthentication();
        app.UseMiddleware<RequestIntegrityMiddleware>();
        app.UseAuthorization();
        return app;
    }

    private static void ConfigureCookie(CookieAuthenticationOptions cookie, AuthConfig auth)
    {
        // The __Host- prefix makes the browser refuse a cookie of that name set by a sibling host or with a Domain: it needs Secure.
        cookie.Cookie.Name = auth.SecureCookie ? "__Host-orchestrarr.session" : "orchestrarr.session";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Strict;
        cookie.Cookie.SecurePolicy = auth.SecureCookie ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        cookie.ExpireTimeSpan = auth.Settings.SessionIdle;
        cookie.SlidingExpiration = true;
        cookie.Events.OnValidatePrincipal = ValidateSession;
        // An API answers with a status, never with a redirect to a login page.
        cookie.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        cookie.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// A cookie is valid while its session is: signing out, a new password and the end of the absolute lifetime end the session on the
    /// server, and a cookie made from a copy of the key ring has no session behind it. An account that is gone or disabled has none either.
    /// </summary>
    private static async Task ValidateSession(CookieValidatePrincipalContext context)
    {
        var services = context.HttpContext.RequestServices;
        var accountId = context.Principal?.AccountId();
        var sessionId = context.Principal?.FindFirst(OrchestrarrClaims.Session)?.Value;
        var account = accountId is { } id ? services.GetRequiredService<IAccountStore>().Get(id) : null;
        if (account is not { Enabled: true } || sessionId is null || services.GetRequiredService<SessionService>().AccountOf(sessionId) != account.Id)
        {
            context.RejectPrincipal();
            // The browser is told to drop the cookie, so that it does not send a dead one until it expires
            await context.HttpContext.SignOutAsync(OrchestrarrClaims.Cookie);
            return;
        }

        context.HttpContext.Items[AccountClaimsTransformation.AccountItem] = account;
    }

    /// <summary>The folder of the key ring: a credential (a copy of it, with an account id, is a cookie), so only its owner may read it.</summary>
    private static DirectoryInfo PrivateFolder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return Directory.CreateDirectory(path);
        }

        var folder = Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // A folder that was already there (a restored backup, a mounted volume) keeps the mode it had unless it is set again.
        folder.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        return folder;
    }
}
