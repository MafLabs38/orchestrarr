using System.Security.Claims;
using Orchestrarr.Core.Accounts;

namespace Orchestrarr.Host.Accounts;

/// <summary>The accounts and the roles, for whoever holds <see cref="Permissions.AccountsManage"/> (ADR 0018).</summary>
internal static class AccountEndpoints
{
    public static void MapAccountEndpoints(this RouteGroupBuilder api)
    {
        var accounts = api.MapGroup("/accounts").RequireAuthorization(Permissions.AccountsManage);

        accounts.MapGet("/", (AccountService service) => AccountResults.Run(() => Results.Ok(service.List().Select(ToDto).ToList())));

        accounts.MapPost("/", (CreateAccountRequest request, HttpContext context, AccountService service) => AccountResults.Run(() =>
        {
            var created = service.Create(request.Name ?? string.Empty, request.Role ?? string.Empty, request.Password ?? string.Empty, Actor(context));
            return Results.Json(ToDto(created), statusCode: StatusCodes.Status201Created);
        }));

        accounts.MapPut("/{id:guid}", (Guid id, UpdateAccountRequest request, HttpContext context, AccountService service, IAccountStore store) => AccountResults.Run(() =>
        {
            var current = store.Existing(id);
            return Results.Ok(ToDto(service.Update(id, request.Role ?? current.Role, request.Enabled ?? current.Enabled, Actor(context))));
        }));

        accounts.MapPost("/{id:guid}/password", (Guid id, SetPasswordRequest request, HttpContext context, AccountService service) => AccountResults.Run(() =>
        {
            service.SetPassword(id, request.Password ?? string.Empty, Actor(context));
            return Results.NoContent();
        }));

        accounts.MapDelete("/{id:guid}", (Guid id, HttpContext context, AccountService service) => AccountResults.Run(() =>
        {
            service.Delete(id, Actor(context));
            return Results.NoContent();
        }));

        api.MapGet("/roles", () => Results.Ok(Roles.All.Select(role => new RoleDto(role.Name, role.Description, [.. role.Permissions.Order(StringComparer.Ordinal)])).ToList()))
            .RequireAuthorization(Permissions.AccountsManage);
    }

    private static string Actor(HttpContext context) => context.User.FindFirst(ClaimTypes.NameIdentifier)!.Value;

    private static AccountDto ToDto(Account account) => new(account.Id, account.Name, account.Role, account.Enabled, account.CreatedAt);
}
