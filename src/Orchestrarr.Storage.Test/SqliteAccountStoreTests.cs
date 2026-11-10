using Microsoft.Data.Sqlite;
using Orchestrarr.Core.Accounts;

namespace Orchestrarr.Storage.Test;

public sealed class SqliteAccountStoreTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDatabase _file = new();

    public void Dispose() => _file.Dispose();

    private static Account Account(string name = "alice", string role = Roles.Operator, bool enabled = true) => new(Guid.NewGuid(), name, role, enabled, Noon);

    [Fact]
    public void An_account_is_read_back_by_id_and_by_name_whatever_the_case_with_its_hash_apart()
    {
        var store = new SqliteAccountStore(_file.Open());
        var account = Account();
        store.Add(account, "hash-1");

        store.Get(account.Id).Should().Be(account);
        var credentials = store.GetCredentials("ALICE")!;
        credentials.Account.Should().Be(account);
        credentials.PasswordHash.Should().Be("hash-1");
        store.GetCredentials("nobody").Should().BeNull();
        store.Get(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public void A_name_is_unique_whatever_its_case()
    {
        var store = new SqliteAccountStore(_file.Open());
        store.Add(Account("alice"), "h");

        var again = () => store.Add(Account("ALICE"), "h");

        again.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Conflict);
        store.Count().Should().Be(1);
    }

    [Fact]
    public void The_role_the_state_and_the_hash_change_and_the_counts_follow()
    {
        var store = new SqliteAccountStore(_file.Open());
        var admin = Account("root", Roles.Admin);
        store.Add(admin, "h");
        store.Add(Account("bob", Roles.Reader), "h");
        store.CountEnabledAdmins().Should().Be(1);

        store.Update(admin.Id, Roles.Admin, enabled: false).Should().BeTrue();
        store.SetPasswordHash(admin.Id, "hash-2").Should().BeTrue();

        store.CountEnabledAdmins().Should().Be(0);
        store.GetCredentials("root")!.PasswordHash.Should().Be("hash-2");
        store.Update(Guid.NewGuid(), Roles.Admin, true).Should().BeFalse();
        store.List().Select(account => account.Name).Should().Equal("bob", "root");
    }

    [Fact]
    public void Removing_an_account_removes_its_sessions_with_it()
    {
        var database = _file.Open();
        var accounts = new SqliteAccountStore(database);
        var sessions = new SqliteSessionStore(database);
        var account = Account();
        accounts.Add(account, "h");
        sessions.Add("s1", account.Id, Noon, Noon.AddDays(1));

        accounts.Remove(account.Id).Should().BeTrue();

        sessions.AccountOf("s1", Noon).Should().BeNull();
        accounts.Remove(account.Id).Should().BeFalse();
    }

    [Fact]
    public void A_session_is_valid_until_it_expires_and_can_be_ended_alone_or_with_all_the_others_of_the_account()
    {
        var database = _file.Open();
        var accounts = new SqliteAccountStore(database);
        var sessions = new SqliteSessionStore(database);
        var alice = Account("alice");
        var bob = Account("bob");
        accounts.Add(alice, "h");
        accounts.Add(bob, "h");
        sessions.Add("a1", alice.Id, Noon, Noon.AddHours(1));
        sessions.Add("a2", alice.Id, Noon, Noon.AddHours(5));
        sessions.Add("b1", bob.Id, Noon, Noon.AddHours(5));

        sessions.AccountOf("a1", Noon.AddMinutes(59)).Should().Be(alice.Id);
        sessions.AccountOf("a1", Noon.AddHours(1)).Should().BeNull("a session ends at its expiry");

        sessions.RemoveAll(alice.Id, except: "a2");
        sessions.AccountOf("a2", Noon).Should().Be(alice.Id);
        sessions.AccountOf("a1", Noon).Should().BeNull();
        sessions.AccountOf("b1", Noon).Should().Be(bob.Id, "another account keeps its session");

        sessions.RemoveAll(alice.Id, except: null);
        sessions.AccountOf("a2", Noon).Should().BeNull();
        sessions.Remove("b1");
        sessions.AccountOf("b1", Noon).Should().BeNull();
    }

    [Fact]
    public void Expired_sessions_are_forgotten()
    {
        var database = _file.Open();
        var accounts = new SqliteAccountStore(database);
        var sessions = new SqliteSessionStore(database);
        var account = Account();
        accounts.Add(account, "h");
        sessions.Add("old", account.Id, Noon, Noon.AddHours(1));
        sessions.Add("new", account.Id, Noon, Noon.AddDays(1));

        sessions.RemoveExpired(Noon.AddHours(2));

        sessions.AccountOf("old", Noon).Should().BeNull();
        sessions.AccountOf("new", Noon).Should().Be(account.Id);
    }

    [Fact]
    public void Accounts_survive_a_restart_and_a_version_2_database_gains_the_tables()
    {
        var database = _file.Open();
        var account = Account();
        new SqliteAccountStore(database).Add(account, "h");

        var reopened = _file.Reopen();
        new SqliteAccountStore(reopened).Get(account.Id).Should().Be(account);

        using (var connection = new SqliteConnection($"Data Source={_file.Path}"))
        {
            connection.Open();
            SqliteDatabase.Run(connection, "DROP TABLE sessions; DROP TABLE accounts; PRAGMA user_version = 2;");
        }

        var migrated = _file.Reopen();
        var store = new SqliteAccountStore(migrated);
        store.Count().Should().Be(0);
        store.Add(Account(), "h");
        store.Count().Should().Be(1);
    }
}
