using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Host.Live;
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

        // Before the body is read: the limit is set (an endpoint filter runs after the binding, which is too late, Kestrel refuses to change it)
        // and the token is checked, so that nobody without one has a body read or parsed
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api/v1/executor", StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }
            context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = MaxBody;
            if (ExecutorNamed(context, settings) is not { } name)
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await Results.Json(new ErrorResponse(ApiErrors.NotSignedIn), statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
                return;
            }
            context.Items[ExecutorItem] = name;
            await next(context);
        });

        var group = app.MapGroup("/api/v1/executor");
        // The routes of the executors carry their own token, not a session: the sign-in of people does not apply to them
        group.AllowAnonymous();

        group.MapPost("/claim", async (ClaimRequest request, HttpContext context, IBatchQueue queue, ExecutorRegistry registry, ILiveNotifier live, CancellationToken ct) =>
        {
            if (request.Hello is null)
            {
                return Results.Json(new ErrorResponse("invalid"), statusCode: StatusCodes.Status400BadRequest);
            }
            var name = (string)context.Items[ExecutorItem]!;
            registry.Seen(name, request.Hello);
            var claim = await queue.ClaimAsync(name, ct);
            if (claim.Status == ClaimStatus.Claimed && claim.Delivery is { } delivery)
            {
                live.Publish(LiveTopics.Runs, delivery.Batch.Id.ToString("D"));
                live.Publish(LiveTopics.Approvals);
            }
            return claim.Status == ClaimStatus.Unavailable ? Results.Json(new ErrorResponse(ApiErrors.StorageUnavailable), statusCode: StatusCodes.Status503ServiceUnavailable) : Results.Ok(claim);
        });

        // What an executor reads while it runs: the global stop reaches a task that is going on, not only the next one
        group.MapGet("/stop", (Orchestrarr.Core.Approvals.IKillSwitchControl killSwitch) => Results.Ok(new StopStatus(killSwitch.Engaged)));

        group.MapPost("/runs/{id:guid}/events", async (Guid id, AppendEventsRequest request, HttpContext context, IBatchQueue queue, ILiveNotifier live, CancellationToken ct) =>
            Answer(await queue.AppendEventsAsync(id, (string)context.Items[ExecutorItem]!, request.Events ?? [], ct), () => live.Publish(LiveTopics.Runs, id.ToString("D"))));

        group.MapPost("/runs/{id:guid}/complete", async (Guid id, CompleteRequest request, HttpContext context, IBatchQueue queue, ILiveNotifier live, CancellationToken ct) =>
            Answer(await queue.CompleteAsync(id, (string)context.Items[ExecutorItem]!, request.Outcome, ct), () =>
            {
                live.Publish(LiveTopics.Runs, id.ToString("D"));
                live.Publish(LiveTopics.Approvals);
                live.Publish(LiveTopics.Audit);
            }));
    }

    private static string? ExecutorNamed(HttpContext http, TasksSettings settings)
    {
        var presented = http.Request.Headers.Authorization.ToString();
        if (!presented.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presented["Bearer ".Length..].Trim()));
        // Every credential is compared, in constant time, whatever the first answer is
        string? name = null;
        foreach (var credential in settings.Executors.Where(credential => CryptographicOperations.FixedTimeEquals(hash, credential.TokenHash)))
        {
            name = credential.Name;
        }
        return name;
    }

    private static IResult Answer(QueueResult result, Action? whenOk = null)
    {
        if (result == QueueResult.Ok)
        {
            whenOk?.Invoke();
        }
        return AnswerOf(result);
    }

    private static IResult AnswerOf(QueueResult result) => result switch
    {
        QueueResult.Ok => Results.NoContent(),
        QueueResult.NotFound => Results.NotFound(),
        QueueResult.NotYours => Results.Json(new ErrorResponse("notYours"), statusCode: StatusCodes.Status409Conflict),
        QueueResult.Invalid => Results.Json(new ErrorResponse("invalid"), statusCode: StatusCodes.Status422UnprocessableEntity),
        _ => Results.Json(new ErrorResponse(ApiErrors.StorageUnavailable), statusCode: StatusCodes.Status503ServiceUnavailable),
    };
}
