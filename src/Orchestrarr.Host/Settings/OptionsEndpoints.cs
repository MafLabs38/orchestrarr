using System.Security.Claims;
using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Settings;
using Orchestrarr.Host.Accounts;
using Orchestrarr.Host.Approvals;

namespace Orchestrarr.Host.Settings;

public sealed record SetOptionRequest(bool? Value, string? Password);

/// <summary>
/// The options of the product (ADR 0023). Everybody who signs in reads them; changing one needs <see cref="Permissions.OptionsManage"/>. Turning one that
/// lowers security off needs, on top, that the host was started to allow it, and the password of the person asking: a stolen session alone is not enough.
/// </summary>
internal static class OptionsEndpoints
{
    public static void MapOptionsEndpoints(this RouteGroupBuilder api)
    {
        var options = api.MapGroup("/options");

        options.MapGet("/", (OptionsService service) => Results.Ok(service.List())).RequireAuthorization(Permissions.FleetRead);

        options.MapPut("/{id}", async (string id, SetOptionRequest request, HttpContext context, OptionsService service, SignInService signIn, CancellationToken ct) =>
        {
            if (request.Value is not { } value)
            {
                return Results.Json(new ErrorResponse("invalid"), statusCode: StatusCodes.Status400BadRequest);
            }
            if (context.User.AccountId() is not { } account)
            {
                return Results.Unauthorized();
            }

            // Lowering security is checked before the password is asked for, so that a locked host says "locked" and not "wrong password"
            if (!value && service.List().FirstOrDefault(option => option.Id == id) is { CanTurnOff: false } locked)
            {
                return Refusal(locked.LockedReason == "configuration" ? OptionRefusal.SetByConfiguration : OptionRefusal.LockedByHost);
            }
            if (!value)
            {
                if (string.IsNullOrEmpty(request.Password))
                {
                    return Results.Json(new ErrorResponse("passwordRequired"), statusCode: StatusCodes.Status400BadRequest);
                }
                if (!await signIn.VerifyPasswordAsync(account, request.Password, ct))
                {
                    return Results.Json(new ErrorResponse("invalidCredentials"), statusCode: StatusCodes.Status401Unauthorized);
                }
            }

            var refusal = await service.SetAsync(id, value, context.User.FindFirst(ClaimTypes.NameIdentifier)!.Value, ct);
            return refusal is null ? Results.Ok(service.List()) : Refusal(refusal.Value);
        }).RequireAuthorization(Permissions.OptionsManage);
    }

    private static IResult Refusal(OptionRefusal refusal) => refusal switch
    {
        OptionRefusal.UnknownOption => Results.Json(new ErrorResponse("unknownOption"), statusCode: StatusCodes.Status404NotFound),
        OptionRefusal.LockedByHost => Results.Json(new ErrorResponse("lockedByHost"), statusCode: StatusCodes.Status403Forbidden),
        OptionRefusal.SetByConfiguration => Results.Json(new ErrorResponse("setByConfiguration"), statusCode: StatusCodes.Status403Forbidden),
        _ => Results.Json(new ErrorResponse(ApiErrors.StorageUnavailable), statusCode: StatusCodes.Status503ServiceUnavailable),
    };
}
