namespace Orchestrarr.Host.SignIn;

/// <summary>
/// What every answer says to the browser about itself. The interface is never framed by another page: a click on it, with the session of
/// the person, is the person's own.
/// </summary>
internal static class SecurityHeaders
{
    public static void UseSecurityHeaders(this WebApplication app) =>
        app.Use(async (context, next) =>
        {
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await next(context);
        });
}
