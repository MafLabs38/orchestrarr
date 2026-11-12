namespace Orchestrarr.Host.Live;

/// <summary>
/// Tells every screen that something changed once a write of the API has succeeded (an approval, the stop, an option, an account): the topics it touches are
/// announced (ADR 0025). A read, a refused write, the routes of the runners (they announce what they touch themselves), the channel itself and the sign-in
/// routes (a login changes nothing a screen shows) announce nothing.
/// </summary>
public sealed class LiveChangeMiddleware(RequestDelegate next, ILiveNotifier live)
{
    private static readonly string[] AfterAWrite = [LiveTopics.Runs, LiveTopics.Approvals, LiveTopics.Audit, LiveTopics.Status];

    private static readonly string[] OwnTopics = [LiveTopics.Accounts, LiveTopics.Options];

    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);
        var path = context.Request.Path;
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method)
            || context.Response.StatusCode >= StatusCodes.Status400BadRequest
            || !path.StartsWithSegments("/api/v1")
            || path.StartsWithSegments(LiveHub.Path) || path.StartsWithSegments("/api/v1/runner") || path.StartsWithSegments("/api/v1/auth"))
        {
            return;
        }
        foreach (var topic in AfterAWrite)
        {
            live.Publish(topic);
        }
        foreach (var topic in OwnTopics.Where(topic => path.StartsWithSegments($"/api/v1/{topic}")))
        {
            live.Publish(topic);
        }
    }
}
