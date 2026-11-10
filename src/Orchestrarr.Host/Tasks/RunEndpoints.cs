using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Host.Approvals;

namespace Orchestrarr.Host.Tasks;

/// <summary>What a person sees of the runs (ADR 0017): the list, and the timeline of one. Nothing here changes anything, and no code of a second factor is ever in it.</summary>
internal static class RunEndpoints
{
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 500;

    public static void MapRunEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/runs", (IBatchQueue queue, int? limit) => WhenReadable(() => Results.Ok(queue.Runs(Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize))))).RequireAuthorization(Permissions.FleetRead);
        api.MapGet("/runs/{id:guid}", (IBatchQueue queue, Guid id) => WhenReadable(() => queue.Find(id) is { } run ? Results.Ok(run) : Results.NotFound())).RequireAuthorization(Permissions.FleetRead);
    }

    private static IResult WhenReadable(Func<IResult> read)
    {
        try
        {
            return read();
        }
        catch (StorageUnavailableException)
        {
            return Results.Json(new ErrorResponse(ApiErrors.StorageUnavailable), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
