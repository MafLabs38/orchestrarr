using System.Globalization;
using Microsoft.Data.Sqlite;
using Orchestrarr.Core.Accounts;

namespace Orchestrarr.Storage;

/// <summary>The accounts in the SQLite file (ADR 0018). A name is unique whatever its case. Every call can wait for the file, no longer than its busy timeout.</summary>
public sealed class SqliteAccountStore(SqliteDatabase database) : IAccountStore
{
    private const string Columns = "id, name, role, enabled, created_at";

    public void Add(Account account, string passwordHash) => SqliteDatabase.Guard(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO accounts (id, name, role, enabled, password_hash, created_at) VALUES ($id, $name, $role, $enabled, $hash, $createdAt);";
        command.Parameters.AddWithValue("$id", account.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", account.Name);
        command.Parameters.AddWithValue("$role", account.Role);
        command.Parameters.AddWithValue("$enabled", account.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$hash", passwordHash);
        command.Parameters.AddWithValue("$createdAt", SqliteDatabase.Format(account.CreatedAt));
        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException failure) when (SqliteDatabase.IsDuplicate(failure))
        {
            throw new AccountRejectedException(AccountProblem.Conflict, $"The name '{account.Name}' is taken.");
        }

        return true;
    });

    public Account? Get(Guid id) => SqliteDatabase.Guard(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM accounts WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    });

    public AccountCredentials? GetCredentials(string name) => SqliteDatabase.Guard(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns}, password_hash FROM accounts WHERE name = $name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new AccountCredentials(Read(reader), reader.GetString(5)) : null;
    });

    public IReadOnlyList<Account> List() => SqliteDatabase.Guard<IReadOnlyList<Account>>(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM accounts ORDER BY name COLLATE NOCASE;";
        using var reader = command.ExecuteReader();
        var accounts = new List<Account>();
        while (reader.Read())
        {
            accounts.Add(Read(reader));
        }

        return accounts;
    });

    public bool Update(Guid id, string role, bool enabled) => Change("UPDATE accounts SET role = $role, enabled = $enabled WHERE id = $id;", id, command =>
    {
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
    });

    public bool SetPasswordHash(Guid id, string passwordHash) =>
        Change("UPDATE accounts SET password_hash = $hash WHERE id = $id;", id, command => command.Parameters.AddWithValue("$hash", passwordHash));

    public bool Remove(Guid id) => Change("DELETE FROM accounts WHERE id = $id;", id, _ => { });

    public int Count() => Scalar("SELECT COUNT(*) FROM accounts;");

    public int CountEnabledAdmins() => Scalar($"SELECT COUNT(*) FROM accounts WHERE enabled = 1 AND role = '{Roles.Admin}';");

    private bool Change(string sql, Guid id, Action<SqliteCommand> bind) => SqliteDatabase.Guard(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        bind(command);
        return command.ExecuteNonQuery() > 0;
    });

    private int Scalar(string sql) => SqliteDatabase.Guard(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    private static Account Read(SqliteDataReader reader) =>
        new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0, SqliteDatabase.Parse(reader.GetString(4)));
}

/// <summary>The sessions in the SQLite file. The id is already a hash when it gets here (<see cref="SessionService"/>).</summary>
public sealed class SqliteSessionStore(SqliteDatabase database) : ISessionStore
{
    public void Add(string id, Guid accountId, DateTimeOffset createdAt, DateTimeOffset expiresAt) => Run(
        "INSERT INTO sessions (id, account_id, created_at, expires_at) VALUES ($id, $account, $createdAt, $expiresAt);",
        command =>
        {
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$account", accountId.ToString("D"));
            command.Parameters.AddWithValue("$createdAt", SqliteDatabase.Format(createdAt));
            command.Parameters.AddWithValue("$expiresAt", SqliteDatabase.Format(expiresAt));
        });

    public Guid? AccountOf(string id, DateTimeOffset now) => SqliteDatabase.Guard(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_id FROM sessions WHERE id = $id AND expires_at > $now;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", SqliteDatabase.Format(now));
        return command.ExecuteScalar() is string account ? Guid.Parse(account) : (Guid?)null;
    });

    public void Remove(string id) => Run("DELETE FROM sessions WHERE id = $id;", command => command.Parameters.AddWithValue("$id", id));

    public void RemoveAll(Guid accountId, string? except) => Run(
        "DELETE FROM sessions WHERE account_id = $account AND ($except IS NULL OR id <> $except);",
        command =>
        {
            command.Parameters.AddWithValue("$account", accountId.ToString("D"));
            command.Parameters.AddWithValue("$except", except is null ? DBNull.Value : except);
        });

    public void RemoveExpired(DateTimeOffset now) =>
        Run("DELETE FROM sessions WHERE expires_at <= $now;", command => command.Parameters.AddWithValue("$now", SqliteDatabase.Format(now)));

    private void Run(string sql, Action<SqliteCommand> bind) => SqliteDatabase.Guard(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        command.ExecuteNonQuery();
        return true;
    });
}
