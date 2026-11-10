using Microsoft.AspNetCore.Identity;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Accounts;

/// <summary>
/// The accounts: creating them, changing a role, a password or the state of an account. It keeps the rules of ADR 0018: a role is one of
/// the built-in ones, a password is long enough, the last enabled administrator stays, a new password ends the other sessions, and what
/// changes is traced. Signing in is <see cref="SignInService"/>.
/// </summary>
public sealed class AccountService(IAccountStore accounts, IAccountAudit audit, LoginThrottle throttle, SessionService sessions, IPasswordHasher<Account> hasher, TimeProvider clock)
{
    private readonly Lock _administrators = new();

    public Account Create(string name, string role, string password, string actor)
    {
        name = name.Trim();
        if (!AccountNames.IsValid(name))
        {
            throw Invalid($"A name is 2 to {AccountNames.MaxLength} letters, digits, dots, dashes or underscores, and starts with a letter or a digit.");
        }

        CheckRole(role);
        var hash = hasher.HashPassword(Account.Placeholder(name), CheckedPassword(password, name));
        var account = new Account(Guid.NewGuid(), name, role, Enabled: true, clock.GetUtcNow());
        accounts.Add(account, hash);
        audit.Record(actor, AccountEvents.AccountCreated, name, $"role {role}");
        return account;
    }

    public IReadOnlyList<Account> List() => accounts.List();

    /// <summary>
    /// Changes the role and the state of an account in one step: both are checked first, and nothing is written when one is refused. A value left
    /// out (null) keeps the account's current one, read under the lock, so that two changes of two different fields never undo each other.
    /// </summary>
    public Account Update(Guid id, string? newRole, bool? newEnabled, string actor)
    {
        lock (_administrators)
        {
            var account = accounts.Existing(id);
            var role = newRole ?? account.Role;
            var enabled = newEnabled ?? account.Enabled;
            CheckRole(role);
            KeepOneAdmin(account, stillAdmin: enabled && role == Roles.Admin);
            if (!accounts.Update(id, role, enabled))
            {
                throw NoSuchAccount();
            }

            if (role != account.Role)
            {
                audit.Record(actor, AccountEvents.AccountRoleChanged, account.Name, $"{account.Role} to {role}");
            }

            if (enabled != account.Enabled)
            {
                if (!enabled)
                {
                    // A disabled account is the answer to a stolen cookie: enabling it again must not bring the cookie back.
                    sessions.EndAll(id);
                }

                audit.Record(actor, enabled ? AccountEvents.AccountEnabled : AccountEvents.AccountDisabled, account.Name);
            }

            return account with { Role = role, Enabled = enabled };
        }
    }

    /// <summary>Sets a password and ends the sessions of the account, except the one given (the person who changes their own keeps theirs).</summary>
    public void SetPassword(Guid id, string password, string actor, string? keepSession = null)
    {
        var account = accounts.Existing(id);
        var hash = hasher.HashPassword(account, CheckedPassword(password, account.Name));
        // The sessions end before the password changes (if the process stopped between the two, nobody keeps a session) and after (a sign-in
        // that checked the old password and started its session in between does not keep it).
        sessions.EndAll(id, keepSession);
        if (!accounts.SetPasswordHash(id, hash))
        {
            throw NoSuchAccount();
        }

        sessions.EndAll(id, keepSession);
        throttle.Succeeded(account.Name);
        throttle.Succeeded(SignInService.PasswordCheckKey(account.Name));
        audit.Record(actor, AccountEvents.AccountPasswordChanged, account.Name);
    }

    /// <summary>Break-glass, for the day nobody can sign in: sets the password, enables the account, lifts its lockout and ends its sessions.</summary>
    public void Reset(string name, string password)
    {
        var credentials = accounts.GetCredentials(name.Trim()) ?? throw new AccountRejectedException(AccountProblem.NotFound, $"There is no account named '{name}'.");
        var account = credentials.Account;
        var hash = hasher.HashPassword(account, CheckedPassword(password, account.Name));
        lock (_administrators)
        {
            // The sessions go first: a reset is for the day a cookie may be stolen, and it must not leave one if it stops half way.
            var current = accounts.Existing(account.Id);
            sessions.EndAll(account.Id);
            accounts.SetPasswordHash(account.Id, hash);
            accounts.Update(account.Id, current.Role, enabled: true);
            sessions.EndAll(account.Id);
        }

        throttle.Succeeded(account.Name);
        throttle.Succeeded(SignInService.PasswordCheckKey(account.Name));
        audit.Record(AuditEvents.System, AccountEvents.AccountReset, account.Name);
    }

    public void Delete(Guid id, string actor)
    {
        lock (_administrators)
        {
            var account = accounts.Existing(id);
            KeepOneAdmin(account, stillAdmin: false);
            sessions.EndAll(id);
            accounts.Remove(id);
            audit.Record(actor, AccountEvents.AccountDeleted, account.Name);
        }
    }

    /// <summary>Refuses a change that would leave no enabled administrator. Called with the administrators lock held.</summary>
    private void KeepOneAdmin(Account account, bool stillAdmin)
    {
        var isAdminNow = account.Enabled && account.Role == Roles.Admin;
        if (isAdminNow && !stillAdmin && accounts.CountEnabledAdmins() <= 1)
        {
            throw new AccountRejectedException(AccountProblem.Conflict, "The last enabled administrator cannot be removed, disabled or demoted.");
        }
    }

    private static void CheckRole(string role)
    {
        if (Roles.Find(role) is null)
        {
            throw Invalid($"'{role}' is not a role.");
        }
    }

    private static string CheckedPassword(string? password, string name)
    {
        if (password is null)
        {
            throw Invalid("A password is required.");
        }

        return PasswordPolicy.Problem(password, name) is { } problem ? throw Invalid(problem) : password;
    }

    private static AccountRejectedException NoSuchAccount() => new(AccountProblem.NotFound, "There is no such account.");

    private static AccountRejectedException Invalid(string message) => new(AccountProblem.Invalid, message);
}
