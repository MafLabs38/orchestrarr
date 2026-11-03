using Orchestrarr.Host.Approvals;

namespace Orchestrarr.Host.SignIn;

/// <summary>
/// Refuses a change that a page of another site makes with the session of the person: a request that is not a read must come from this
/// application's own origin. The session cookie is SameSite Lax, which already keeps it from a cross-site POST; this is the second line.
/// </summary>
internal static class SameOriginGuard
{
    private static readonly HashSet<string> Reads = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    public static void UseSameOriginGuard(this WebApplication app, SignInOptions options) =>
        app.Use(async (context, next) =>
        {
            if (!IsAllowed(context.Request, options))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new ErrorResponse(ApiErrors.OriginNotAllowed));
                return;
            }
            await next(context);
        });

    internal static bool IsAllowed(HttpRequest request, SignInOptions options)
    {
        if (Reads.Contains(request.Method))
        {
            return true;
        }
        // A browser says where a request comes from. One that says "another site" is refused; so is an origin that is not ours. A client
        // that is no browser says nothing, and has no session cookie to abuse.
        if (request.Headers.TryGetValue("Origin", out var origin))
        {
            return string.Equals(origin.ToString(), options.Origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
        }
        return !request.Headers.TryGetValue("Sec-Fetch-Site", out var site) || site == "same-origin" || site == "none";
    }
}
