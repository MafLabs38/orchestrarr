using Microsoft.Data.Sqlite;
using Orchestrarr.Core.Settings;

namespace Orchestrarr.Storage.Test;

public sealed class SqliteOptionStoreTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDatabase _file = new();

    public void Dispose() => _file.Dispose();

    [Fact]
    public void An_option_nobody_set_is_unknown_and_a_set_value_is_kept_across_a_restart()
    {
        var store = new SqliteOptionStore(_file.Open());
        store.Get(OptionIds.RequireSecondFactor).Should().BeNull();

        store.TrySet(OptionIds.RequireSecondFactor, false, Noon, "root").Should().BeTrue();
        store.TrySet(OptionIds.RequireSecondFactor, false, Noon, "root").Should().BeTrue("setting twice is an update, not a conflict");

        new SqliteOptionStore(_file.Reopen()).Get(OptionIds.RequireSecondFactor).Should().BeFalse();
        store.TrySet(OptionIds.RequireSecondFactor, true, Noon, "root");
        new SqliteOptionStore(_file.Reopen()).Get(OptionIds.RequireSecondFactor).Should().BeTrue();
    }

    [Fact]
    public void A_database_of_the_previous_version_gains_the_table_and_loses_nothing()
    {
        _file.Open();
        using (var connection = new SqliteConnection($"Data Source={_file.Path}"))
        {
            connection.Open();
            SqliteDatabase.Run(connection, "DROP TABLE options; PRAGMA user_version = 3;");
        }

        var migrated = new SqliteOptionStore(_file.Reopen());

        migrated.Get(OptionIds.RequireSecondFactor).Should().BeNull();
        migrated.TrySet(OptionIds.RequireSecondFactor, false, Noon, "root").Should().BeTrue();
    }
}
