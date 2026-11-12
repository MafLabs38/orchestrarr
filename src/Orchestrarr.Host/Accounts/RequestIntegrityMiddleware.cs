using Microsoft.AspNetCore.Mvc;

namespace Orchestrarr.Host.Accounts;

/// <summary>
/// The protection against cross-site requests (ADR 0018), on top of <c>SameSite=Strict</c>: a write to the API from a person must carry the
/// custom header that only the interface, and a tool that means to, sends. A browser cannot add it to a form or a link. The routes of the
/// runners carry their own token and are not a person's.
/// </summary>
public sealed class RequestIntegrityMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Orchestrarr-Request";
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var isWrite = !SafeMethods.Contains(context.Request.Method);
        var isPerson = path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) && !path.StartsWithSegments("/api/v1/runner", StringComparison.OrdinalIgnoreCase);
        if (isWrite && isPerson && !context.Request.Headers.ContainsKey(HeaderName))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsJsonAsync(
                new ProblemDetails { Status = StatusCodes.Status403Forbidden, Title = $"A write needs the {HeaderName} header." },
                options: null,
                contentType: "application/problem+json",
                cancellationToken: context.RequestAborted);
        }

        return next(context);
    }
}
