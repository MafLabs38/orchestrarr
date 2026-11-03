using Orchestrarr.Host.Approvals;

namespace Orchestrarr.Host.SignIn;

/// <summary>
/// Refuses a change that a page of another site makes with the session of the person: a request that is not a read must come from this
/// application's own origin. The session cookie is SameSite Lax, which already keeps it from a cross-site POST; this is the second line.
/// </summary>
internal sealed class SameOriginGuard(RequestDelegate next, SignInSettings settings)
{
    private static readonly HashSet<string> Reads = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsAllowed(context.Request, settings))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ErrorResponse(ApiErrors.OriginNotAllowed), context.RequestAborted);
            return;
        }
        await next(context);
    }

    internal static bool IsAllowed(HttpRequest request, SignInSettings settings)
    {
        if (Reads.Contains(request.Method))
        {
            return true;
        }
        // A browser says where a request comes from, and what it says decides: an origin that is not ours, or "another site", is refused.
        if (request.Headers.TryGetValue("Origin", out var origin))
        {
            return string.Equals(origin.ToString(), settings.Origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
        }
        if (request.Headers.TryGetValue("Sec-Fetch-Site", out var site))
        {
            return site.ToString().Equals("same-origin", StringComparison.OrdinalIgnoreCase) || site.ToString().Equals("none", StringComparison.OrdinalIgnoreCase);
        }
        // Neither header: an old browser, or no browser. A client that is no browser has no session cookie to abuse; a request that carries
        // the session and says nothing about where it comes from is not trusted.
        return !request.Cookies.ContainsKey(settings.SessionCookieName);
    }
}
