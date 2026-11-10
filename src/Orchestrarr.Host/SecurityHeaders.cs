namespace Orchestrarr.Host;

/// <summary>
/// What every answer says to the browser about itself. The interface is never framed by another page: a click on it, with the session of
/// the person, is the person's own. Its scripts, styles and requests come from itself and nowhere else, so that a script that found its way
/// into a page cannot load another or send what it reads elsewhere. Over https the browser is told to keep to it.
/// </summary>
internal static class SecurityHeaders
{
    private const string Policy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; "
        + "object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    private const string StrictTransport = "max-age=31536000";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, bool overHttps) =>
        app.Use(async (context, next) =>
        {
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers.ContentSecurityPolicy = Policy;
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (overHttps)
            {
                context.Response.Headers.StrictTransportSecurity = StrictTransport;
            }
            await next(context);
        });
}
