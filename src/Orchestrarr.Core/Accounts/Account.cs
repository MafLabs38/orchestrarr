namespace Orchestrarr.Core.Accounts;

/// <summary>
/// A person who signs in (ADR 0018): a name, a password (kept as a hash, apart), one role and a state. The id never changes and is what the
/// trail, the approvals and the second factor of the executor call the person: a name can be reused, an id cannot.
/// </summary>
public sealed record Account(Guid Id, string Name, string Role, bool Enabled, DateTimeOffset CreatedAt)
{
    /// <summary>An account that is not stored, for the hasher to bind a hash to a name: a new account's password, and the decoy.</summary>
    public static Account Placeholder(string name) => new(Guid.Empty, name, Roles.Reader, false, DateTimeOffset.UnixEpoch);

    private static readonly IReadOnlySet<string> NoPermissions = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>What the role of the account gives it, now. A role that no longer exists gives nothing.</summary>
    public IReadOnlySet<string> Permissions => Roles.Find(Role)?.Permissions ?? NoPermissions;

    /// <summary>
    /// The identifier the approvals, the trail and the executor's second factor use for this person: the id in its canonical form, which the
    /// order's rule for a subject accepts (letters, digits and <c>. _ - @ : +</c>).
    /// </summary>
    public string Subject => Id.ToString("D");
}

/// <summary>A person who has signed in, and the id of the session that was started for them.</summary>
public sealed record SignedIn(Account Account, string SessionId);

/// <summary>An account with its password hash, for signing in. Never leaves the accounts code.</summary>
public sealed record AccountCredentials(Account Account, string? PasswordHash);
