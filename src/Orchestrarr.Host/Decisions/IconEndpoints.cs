using System.Text.RegularExpressions;
using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Host.Decisions;

/// <summary>
/// The icons of the applications, relayed from the source of the fleet (Portarr serves them): the page asks the host, which holds the key of the source. Only a name made
/// of letters, digits, dots, dashes and underscores is relayed, and the picture is served so that a script inside it cannot run.
/// </summary>
internal static partial class IconEndpoints
{
    public static void MapIconEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/icons/{name}", async (string name, IIconSource icons, WorkItemCatalog catalog, HttpContext context, CancellationToken ct) =>
        {
            // Only an icon that the source itself named for one of its applications: a name made up by someone signed in is never relayed
            if (!ValidName().IsMatch(name) || !(await catalog.GetAsync(refresh: false, ct)).Applications.OrEmpty().Any(app => string.Equals(app.Icon, name, StringComparison.Ordinal)))
            {
                return Results.NotFound();
            }
            var image = await icons.GetAsync(name, ct);
            if (image is null)
            {
                return Results.NotFound();
            }
            // A picture is shown, never run: no script, no navigation to it as a page, and the type is the one given
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.Bytes(image.Content, image.ContentType);
        }).RequireAuthorization(Permissions.FleetRead);
    }

    private static IReadOnlyList<ApplicationInfo> OrEmpty(this IReadOnlyList<ApplicationInfo>? applications) => applications ?? [];

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex ValidName();
}
