namespace Orchestrarr.Core.Accounts;

public interface IAccountStore
{
    /// <summary>Adds an account. Throws <see cref="AccountRejectedException"/> (conflict) when the name is taken, whatever its case.</summary>
    void Add(Account account, string passwordHash);

    Account? Get(Guid id);

    AccountCredentials? GetCredentials(string name);

    IReadOnlyList<Account> List();

    bool Update(Guid id, string role, bool enabled);

    bool SetPasswordHash(Guid id, string passwordHash);

    /// <summary>Removes an account. False when there was none.</summary>
    bool Remove(Guid id);

    int Count();

    /// <summary>How many enabled accounts hold the Admin role: the last one must stay.</summary>
    int CountEnabledAdmins();
}

public interface ISessionStore
{
    void Add(string id, Guid accountId, DateTimeOffset createdAt, DateTimeOffset expiresAt);

    /// <summary>The account of a session that exists and has not expired, or null.</summary>
    Guid? AccountOf(string id, DateTimeOffset now);

    void Remove(string id);

    /// <summary>Ends every session of an account, except one (null: none is kept).</summary>
    void RemoveAll(Guid accountId, string? except);

    /// <summary>Forgets the sessions that expired before this moment.</summary>
    void RemoveExpired(DateTimeOffset now);
}

/// <summary>
/// Where the accounts' events go: the audit trail of the product. A port, so that the accounts code does not know the trail's bounds. An event
/// that cannot be recorded does not stop the sign-in or the change it is about (the trail is bounded and says so itself).
/// </summary>
public interface IAccountAudit
{
    void Record(string actor, string action, string target, string? detail = null);
}

public static class AccountStoreExtensions
{
    /// <summary>The account, or the refusal that says there is none: one wording for every caller.</summary>
    public static Account Existing(this IAccountStore store, Guid id) =>
        store.Get(id) ?? throw new AccountRejectedException(AccountProblem.NotFound, "There is no such account.");
}
