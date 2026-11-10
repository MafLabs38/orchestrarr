using Microsoft.AspNetCore.Identity;

namespace Orchestrarr.Core.Accounts;

/// <summary>
/// Signing in and checking a person's current password (ADR 0018): the lockout, the decoy that makes a wrong name and a wrong password
/// take the same time, the places to hash a password, and the session that starts once the password is right. What happens to an account
/// (its role, its password, its state) is <see cref="AccountService"/>.
/// </summary>
public sealed class SignInService
{
    /// <summary>How many passwords are hashed at the same time: one per core, two to eight. The scarce resource of the host, and what a flood of sign-ins would exhaust.</summary>
    public static readonly int MaxConcurrentHashes = Math.Clamp(Environment.ProcessorCount, 2, 8);
    private static readonly TimeSpan BusyRetryAfter = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _hashSlots = new(MaxConcurrentHashes, MaxConcurrentHashes);
    private readonly IAccountStore _accounts;
    private readonly IAccountAudit _audit;
    private readonly LoginThrottle _throttle;
    private readonly SessionService _sessions;
    private readonly AuditSampler _sampler;
    private readonly IPasswordHasher<Account> _hasher;
    private readonly string _decoyHash;

    public SignInService(IAccountStore accounts, IAccountAudit audit, LoginThrottle throttle, SessionService sessions, AuditSampler sampler, IPasswordHasher<Account> hasher)
    {
        _accounts = accounts;
        _audit = audit;
        _throttle = throttle;
        _sessions = sessions;
        _sampler = sampler;
        _hasher = hasher;

        // Checked when the name is unknown, so that a wrong name and a wrong password take the same time.
        _decoyHash = hasher.HashPassword(Account.Placeholder("decoy"), Guid.NewGuid().ToString("N"));
    }

    /// <summary>How long a sign-in waits for a place to hash its password before it is told to come back later.</summary>
    public TimeSpan HashWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The counter of the wrong current passwords of a person is their own: whoever guesses their name at the sign-in cannot stop them
    /// changing it. A name cannot hold the colon, and the sign-in refuses what a name cannot be, so the two kinds of key never meet.
    /// </summary>
    public static string PasswordCheckKey(string name) => $"password-check:{name}";

    /// <summary>
    /// Signs a person in, once a place to hash the password is free (waiting for one holds no thread, and not for long), and starts their
    /// session. The session is checked after it is started: an account disabled, or a password changed, while the password was being
    /// hashed ends it again, so that a new password or a disabling that ended the sessions a moment earlier cannot leave this one behind.
    /// </summary>
    public async Task<SignedIn?> SignInAsync(string name, string password, CancellationToken cancellationToken)
    {
        using var slot = await TakeHashSlotAsync(cancellationToken);
        if (Check(name, password) is not var (account, hash))
        {
            return null;
        }

        var session = _sessions.Start(account.Id);
        if (_accounts.GetCredentials(account.Name) is { Account.Enabled: true, PasswordHash: { } current } && current == hash)
        {
            // Only now is it a sign-in: the failures before it are forgiven, and the trail says so.
            _throttle.Succeeded(account.Name);
            _audit.Record(account.Subject, AccountEvents.LoginSucceeded, account.Name);
            return new SignedIn(account, session);
        }

        _sessions.End(session);
        _audit.Record(account.Subject, AccountEvents.LoginFailed, account.Name, "the account or its password changed while it was checked");
        return null;
    }

    /// <summary>
    /// Whether a person's password is the one they give, for a change of their own password. It does not sign in, but it counts like a
    /// sign-in: whoever holds a stolen session must not be able to guess the password at the speed of the hash.
    /// </summary>
    public async Task<bool> VerifyPasswordAsync(Guid id, string password, CancellationToken cancellationToken)
    {
        using var slot = await TakeHashSlotAsync(cancellationToken);
        return VerifyPassword(id, password);
    }

    /// <summary>Ends a session: the person signed out.</summary>
    public void EndSession(string sessionId) => _sessions.End(sessionId);

    private bool VerifyPassword(Guid id, string password)
    {
        var account = _accounts.Existing(id);
        var budget = PasswordCheckKey(account.Name);
        Reserve(budget, isAccount: true, account, AccountEvents.OwnChangeLocked);
        var credentials = _accounts.GetCredentials(account.Name);
        if (credentials?.PasswordHash is null || password.Length > PasswordPolicy.MaxLength
            || _hasher.VerifyHashedPassword(account, credentials.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            _audit.Record(account.Subject, AccountEvents.OwnChangeRefused, account.Name);
            return false;
        }

        _throttle.Succeeded(budget);
        return true;
    }

    /// <summary>The account that signed in and the password hash it was checked against, which is the one the sessions are bound to.</summary>
    private (Account Account, string Hash)? Check(string name, string password)
    {
        name = name.Trim();
        // Only what a name can be is counted: a counter is keyed by the name, and the other budgets cannot be reached with a forged one.
        if (!AccountNames.IsValid(name) || password.Length > PasswordPolicy.MaxLength)
        {
            return null;
        }

        var credentials = _accounts.GetCredentials(name);
        Reserve(name, isAccount: credentials is not null, credentials?.Account, AccountEvents.LoginLocked);

        // An unknown name is checked against the decoy: the answer takes the same time.
        var isDecoy = credentials?.PasswordHash is null;
        var (account, hash) = isDecoy ? (Account.Placeholder(name), _decoyHash) : (credentials!.Account, credentials.PasswordHash!);
        var result = _hasher.VerifyHashedPassword(account, hash, password);
        if (isDecoy || !credentials!.Account.Enabled || result == PasswordVerificationResult.Failed)
        {
            // Only a name that exists is audited: an unknown name is anybody's guess, and anybody can send a million.
            if (credentials is not null)
            {
                _audit.Record(credentials.Account.Subject, AccountEvents.LoginFailed, name);
            }

            return null;
        }

        return Rehashed(account, hash, result, password);
    }

    /// <summary>Counts an attempt before its password is checked, or refuses it: a locked name is not checked at all.</summary>
    private void Reserve(string counter, bool isAccount, Account? account, string action)
    {
        if (_throttle.TryReserve(counter, isAccount))
        {
            return;
        }

        // Only a name that exists is traced, and at most once a minute: anybody can send a million attempts on a name
        if (account is not null && _sampler.Allow(action, counter.ToUpperInvariant()))
        {
            _audit.Record(account.Subject, action, account.Name);
        }

        throw new AccountRejectedException(AccountProblem.Locked, "Too many failed attempts. Try again later.", _throttle.LockedFor(counter) ?? LoginThrottle.Window);
    }

    /// <summary>The hash the sessions are bound to: the stored one, or the new one when the cost of hashing has changed since it was made.</summary>
    private (Account Account, string Hash) Rehashed(Account account, string hash, PasswordVerificationResult result, string password)
    {
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            hash = _hasher.HashPassword(account, password);
            _accounts.SetPasswordHash(account.Id, hash);
        }

        return (account, hash);
    }

    /// <summary>A place to hash a password, or a refusal to come back later: the work waits for a few at a time, and not for long.</summary>
    private async Task<SlotReleaser> TakeHashSlotAsync(CancellationToken cancellationToken)
    {
        if (!await _hashSlots.WaitAsync(HashWait, cancellationToken))
        {
            throw new AccountRejectedException(AccountProblem.Busy, "The server is busy checking passwords. Try again in a moment.", BusyRetryAfter);
        }

        return new SlotReleaser(_hashSlots);
    }

    private readonly struct SlotReleaser(SemaphoreSlim slots) : IDisposable
    {
        public void Dispose() => slots.Release();
    }
}

/// <summary>The names of the accounts' events in the trail.</summary>
public static class AccountEvents
{
    public const string LoginSucceeded = "login.succeeded";
    public const string LoginFailed = "login.failed";
    public const string LoginLocked = "login.locked";
    public const string OwnChangeLocked = "password.locked";
    public const string OwnChangeRefused = "password.verify-failed";
    public const string AccountCreated = "account.created";
    public const string AccountRoleChanged = "account.role-changed";
    public const string AccountEnabled = "account.enabled";
    public const string AccountDisabled = "account.disabled";
    public const string AccountOwnSignInChanged = "account.password-changed";
    public const string AccountReset = "account.reset";
    public const string AccountDeleted = "account.deleted";

    /// <summary>The failures: anybody can provoke them, so they have the smaller share of a bounded trail.</summary>
    public static bool IsFailure(string name) => name is LoginFailed or LoginLocked or OwnChangeLocked or OwnChangeRefused;
}
