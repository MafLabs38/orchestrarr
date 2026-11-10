namespace Orchestrarr.Core.Accounts;

/// <summary>The accounts in memory: the sandbox and the tests (a restart forgets them, and the seeder creates the first administrator again).</summary>
public sealed class InMemoryAccountStore : IAccountStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, (Account Account, string Hash)> _accounts = [];

    public void Add(Account account, string passwordHash)
    {
        lock (_gate)
        {
            if (_accounts.Values.Any(entry => string.Equals(entry.Account.Name, account.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new AccountRejectedException(AccountProblem.Conflict, $"The name '{account.Name}' is taken.");
            }

            _accounts[account.Id] = (account, passwordHash);
        }
    }

    public Account? Get(Guid id)
    {
        lock (_gate)
        {
            return _accounts.TryGetValue(id, out var entry) ? entry.Account : null;
        }
    }

    public AccountCredentials? GetCredentials(string name)
    {
        lock (_gate)
        {
            var found = _accounts.Values.FirstOrDefault(entry => string.Equals(entry.Account.Name, name, StringComparison.OrdinalIgnoreCase));
            return found.Account is null ? null : new AccountCredentials(found.Account, found.Hash);
        }
    }

    public IReadOnlyList<Account> List()
    {
        lock (_gate)
        {
            return [.. _accounts.Values.Select(entry => entry.Account).OrderBy(account => account.Name, StringComparer.OrdinalIgnoreCase)];
        }
    }

    public bool Update(Guid id, string role, bool enabled)
    {
        lock (_gate)
        {
            if (!_accounts.TryGetValue(id, out var entry))
            {
                return false;
            }

            _accounts[id] = (entry.Account with { Role = role, Enabled = enabled }, entry.Hash);
            return true;
        }
    }

    public bool SetPasswordHash(Guid id, string passwordHash)
    {
        lock (_gate)
        {
            if (!_accounts.TryGetValue(id, out var entry))
            {
                return false;
            }

            _accounts[id] = (entry.Account, passwordHash);
            return true;
        }
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            return _accounts.Remove(id);
        }
    }

    public int Count()
    {
        lock (_gate)
        {
            return _accounts.Count;
        }
    }

    public int CountEnabledAdmins()
    {
        lock (_gate)
        {
            return _accounts.Values.Count(entry => entry.Account is { Enabled: true, Role: Roles.Admin });
        }
    }
}

/// <summary>The sessions in memory: the sandbox and the tests.</summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (Guid AccountId, DateTimeOffset ExpiresAt)> _sessions = [];

    public void Add(string id, Guid accountId, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        lock (_gate)
        {
            _sessions[id] = (accountId, expiresAt);
        }
    }

    public Guid? AccountOf(string id, DateTimeOffset now)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(id, out var session) && session.ExpiresAt > now ? session.AccountId : null;
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            _sessions.Remove(id);
        }
    }

    public void RemoveAll(Guid accountId, string? except)
    {
        lock (_gate)
        {
            foreach (var id in _sessions.Where(pair => pair.Value.AccountId == accountId && pair.Key != except).Select(pair => pair.Key).ToList())
            {
                _sessions.Remove(id);
            }
        }
    }

    public void RemoveExpired(DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var id in _sessions.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToList())
            {
                _sessions.Remove(id);
            }
        }
    }
}
