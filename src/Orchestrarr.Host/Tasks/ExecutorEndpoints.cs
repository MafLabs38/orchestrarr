using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Host.Approvals;

namespace Orchestrarr.Host.Tasks;

/// <summary>
/// The routes that only an executor uses (ADR 0017): it asks for a batch, sends the lines of its trace and says how it ended. They are not
/// for a person (no session opens them) and not for a browser: each call carries the token of one executor. With no executor configured the
/// routes are not there at all.
/// </summary>
internal static class ExecutorEndpoints
{
    private const string ExecutorItem = "executor";

    // A batch, a trace or an outcome is small: an executor that was taken over cannot send the host something big
    private const long MaxBody = 256 * 1024;

    public static void MapExecutorEndpoints(this WebApplication app, TasksSettings settings)
    {
        if (settings.Executors.Count == 0)
        {
            return;
        }

        var group = app.MapGroup("/api/v1/executor");
        group.AddEndpointFilter((context, next) => Authenticate(context, next, settings));

        group.MapPost("/claim", async (ClaimRequest request, HttpContext context, IBatchQueue queue, ExecutorRegistry registry, CancellationToken ct) =>
        {
            var name = (string)context.Items[ExecutorItem]!;
            registry.Seen(name, request.Hello);
            var claim = await queue.ClaimAsync(name, ct);
            return claim.Status == ClaimStatus.Unavailable ? Results.Json(new ErrorResponse(ApiErrors.StorageUnavailable), statusCode: StatusCodes.Status503ServiceUnavailable) : Results.Ok(claim);
        });

        group.MapPost("/runs/{id:guid}/events", async (Guid id, AppendEventsRequest request, HttpContext context, IBatchQueue queue, CancellationToken ct) =>
            Answer(await queue.AppendEventsAsync(id, (string)context.Items[ExecutorItem]!, request.Events ?? [], ct)));

        group.MapPost("/runs/{id:guid}/complete", async (Guid id, CompleteRequest request, HttpContext context, IBatchQueue queue, CancellationToken ct) =>
            Answer(await queue.CompleteAsync(id, (string)context.Items[ExecutorItem]!, request.Outcome, ct)));
    }

    private static ValueTask<object?> Authenticate(EndpointFilterInvocationContext context, EndpointFilterDelegate next, TasksSettings settings)
    {
        var http = context.HttpContext;
        http.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = MaxBody;
        var presented = http.Request.Headers.Authorization.ToString();
        if (presented.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presented["Bearer ".Length..].Trim()));
            // Every credential is compared, in constant time, whatever the first answer is
            string? name = null;
            foreach (var credential in settings.Executors.Where(credential => CryptographicOperations.FixedTimeEquals(hash, credential.TokenHash)))
            {
                name = credential.Name;
            }
            if (name is not null)
            {
                http.Items[ExecutorItem] = name;
                return next(context);
            }
        }
        http.Response.Headers.WWWAuthenticate = "Bearer";
        return ValueTask.FromResult<object?>(Results.Json(new ErrorResponse(ApiErrors.NotSignedIn), statusCode: StatusCodes.Status401Unauthorized));
    }

    private static IResult Answer(QueueResult result) => result switch
    {
        QueueResult.Ok => Results.NoContent(),
        QueueResult.NotFound => Results.NotFound(),
        QueueResult.NotYours => Results.Json(new ErrorResponse("notYours"), statusCode: StatusCodes.Status409Conflict),
        QueueResult.Invalid => Results.Json(new ErrorResponse("invalid"), statusCode: StatusCodes.Status422UnprocessableEntity),
        _ => Results.Json(new ErrorResponse(ApiErrors.StorageUnavailable), statusCode: StatusCodes.Status503ServiceUnavailable),
    };
}
