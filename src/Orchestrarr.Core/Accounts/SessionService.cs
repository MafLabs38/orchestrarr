using System.Security.Cryptography;

namespace Orchestrarr.Core.Accounts;

/// <summary>
/// The sessions of the people who signed in (ADR 0018). The cookie carries a session id and the server keeps the session: signing out,
/// changing a password and the end of its absolute lifetime end it for real, and a cookie that was forged or copied from a backup of the
/// key ring has no session behind it. The store keeps only a hash of the id: a copy of the database and of the key ring together does not
/// make a cookie either.
/// </summary>
public sealed class SessionService(ISessionStore store, TimeProvider clock, TimeSpan maxLifetime)
{
    /// <summary>Starts a session for an account and returns its id.</summary>
    public string Start(Guid accountId)
    {
        var now = clock.GetUtcNow();
        store.RemoveExpired(now);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        store.Add(SecretHash.Of(id), accountId, now, now + maxLifetime);
        return id;
    }

    /// <summary>The account a session belongs to, while it is valid.</summary>
    public Guid? AccountOf(string sessionId) => store.AccountOf(SecretHash.Of(sessionId), clock.GetUtcNow());

    public void End(string sessionId) => store.Remove(SecretHash.Of(sessionId));

    public void EndAll(Guid accountId, string? except = null) => store.RemoveAll(accountId, except is null ? null : SecretHash.Of(except));
}
