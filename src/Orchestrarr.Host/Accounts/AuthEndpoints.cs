using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Host.Approvals;

namespace Orchestrarr.Host.Accounts;

/// <summary>What a signed-in person sees of themselves, and what their role lets them do.</summary>
public sealed record MeDto(string Subject, string Name, string Role, IReadOnlyList<string> Permissions);

public sealed record LoginRequest(string? Name, string? Password);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record AccountDto(Guid Id, string Name, string Role, bool Enabled, DateTimeOffset CreatedAt);

public sealed record RoleDto(string Name, string Description, IReadOnlyList<string> Permissions);

public sealed record CreateAccountRequest(string? Name, string? Role, string? Password);

public sealed record UpdateAccountRequest(string? Role, bool? Enabled);

public sealed record SetPasswordRequest(string? Password);

/// <summary>The answer of a refused operation on accounts: a stable code, and the words for the person when they can use them.</summary>
public sealed record AccountErrorResponse(string Error, string? Message);

/// <summary>Signing in and out, who I am, my own password (ADR 0018). The sign-in is the only route of the API that needs no session.</summary>
internal static class AuthEndpoints
{
    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");

        auth.MapPost("/login", (LoginRequest request, HttpContext context, SignInService signIn, CancellationToken ct) =>
            AccountResults.Run(async () =>
            {
                if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrEmpty(request.Password))
                {
                    return Results.Json(new AccountErrorResponse("invalid", "A name and a password are required."), statusCode: StatusCodes.Status400BadRequest);
                }

                // The same answer for an unknown name and a wrong password
                if (await signIn.SignInAsync(request.Name, request.Password, ct) is not { } signedIn)
                {
                    return Results.Json(new AccountErrorResponse("invalidCredentials", null), statusCode: StatusCodes.Status401Unauthorized);
                }

                var principal = new ClaimsPrincipal(OrchestrarrClaims.Identity(signedIn.Account, signedIn.SessionId));
                await context.SignInAsync(OrchestrarrClaims.Cookie, principal, new AuthenticationProperties { IsPersistent = false });
                return Results.Ok(Me(signedIn.Account));
            })).AllowAnonymous();

        auth.MapPost("/logout", async (HttpContext context, SignInService signIn) =>
        {
            if (context.User.FindFirst(OrchestrarrClaims.Session)?.Value is { } session)
            {
                signIn.EndSession(session);
            }

            await context.SignOutAsync(OrchestrarrClaims.Cookie);
            return Results.NoContent();
        });

        auth.MapGet("/me", (HttpContext context, IAccountStore accounts) =>
            context.User.AccountId() is { } id && accounts.Get(id) is { } account ? Results.Ok(Me(account)) : Results.Unauthorized());

        auth.MapPost("/password", (ChangePasswordRequest request, HttpContext context, SignInService signIn, AccountService service, CancellationToken ct) =>
            AccountResults.Run(async () =>
            {
                if (context.User.AccountId() is not { } id)
                {
                    return Results.Unauthorized();
                }

                if (string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword))
                {
                    return Results.Json(new AccountErrorResponse("invalid", "The current and the new password are required."), statusCode: StatusCodes.Status400BadRequest);
                }

                if (!await signIn.VerifyPasswordAsync(id, request.CurrentPassword, ct))
                {
                    return Results.Json(new AccountErrorResponse("invalidCredentials", null), statusCode: StatusCodes.Status401Unauthorized);
                }

                // The person keeps their own session; every other one ends
                service.SetPassword(id, request.NewPassword, context.User.FindFirst(ClaimTypes.NameIdentifier)!.Value, context.User.FindFirst(OrchestrarrClaims.Session)?.Value);
                return Results.NoContent();
            }));
    }

    internal static MeDto Me(Account account) => new(account.Subject, account.Name, account.Role, [.. account.Permissions.Order(StringComparer.Ordinal)]);
}

/// <summary>Turns a refused operation on accounts into a status and a code the interface words.</summary>
internal static class AccountResults
{
    public static async Task<IResult> Run(Func<Task<IResult>> work)
    {
        try
        {
            return await work();
        }
        catch (AccountRejectedException rejected)
        {
            return Refused(rejected);
        }
        catch (StorageUnavailableException)
        {
            return Results.Json(new ErrorResponse(ApiErrors.StorageUnavailable), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    public static IResult Run(Func<IResult> work) => Run(() => Task.FromResult(work())).GetAwaiter().GetResult();

    private static IResult Refused(AccountRejectedException rejected)
    {
        var (code, status) = rejected.Problem switch
        {
            AccountProblem.Invalid => ("invalid", StatusCodes.Status400BadRequest),
            AccountProblem.Conflict => ("conflict", StatusCodes.Status409Conflict),
            AccountProblem.NotFound => ("notFound", StatusCodes.Status404NotFound),
            AccountProblem.Locked => ("locked", StatusCodes.Status429TooManyRequests),
            _ => ("busy", StatusCodes.Status429TooManyRequests),
        };
        return new RefusedResult(new AccountErrorResponse(code, rejected.Message), status, rejected.RetryAfter);
    }

    private sealed class RefusedResult(AccountErrorResponse body, int status, TimeSpan? retryAfter) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            if (retryAfter is { } wait)
            {
                httpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            httpContext.Response.StatusCode = status;
            return httpContext.Response.WriteAsJsonAsync(body, httpContext.RequestAborted);
        }
    }
}
