using Microsoft.Data.Sqlite;

namespace Orchestrarr.Storage.Test;

public sealed class SqliteDatabaseTests : IDisposable
{
    private readonly TempDatabase _file = new();

    public void Dispose() => _file.Dispose();

    [Fact]
    public void Opening_makes_the_file_and_its_folder_and_can_be_done_again()
    {
        var nested = Path.Combine(Path.GetDirectoryName(_file.Path)!, "a", "b", "orchestrarr.db");

        SqliteDatabase.Open(nested);
        var again = () => SqliteDatabase.Open(nested);

        File.Exists(nested).Should().BeTrue();
        again.Should().NotThrow();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_path_is_required(string path)
    {
        var open = () => SqliteDatabase.Open(path);

        open.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_file_that_is_not_a_database_is_an_error_at_start()
    {
        _file.Damage();

        var open = () => _file.Open();

        open.Should().Throw<SqliteException>();
    }

    [Fact]
    public void A_database_of_a_newer_version_is_refused_not_damaged()
    {
        var database = _file.Open();
        using (var connection = database.Connect())
        {
            SqliteDatabase.Run(connection, "PRAGMA user_version = 99;");
        }

        var open = () => _file.Open();

        open.Should().Throw<InvalidOperationException>().WithMessage("*newer version*");
    }

    [Fact]
    public void The_moments_are_kept_in_utc_with_a_fixed_width_so_that_sorting_the_text_sorts_the_time()
    {
        var earlier = SqliteDatabase.Format(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(-5)));
        var later = SqliteDatabase.Format(new DateTimeOffset(2026, 11, 3, 15, 30, 0, TimeSpan.Zero));

        earlier.Should().Be("2026-11-03T14:00:00.0000000Z");
        string.CompareOrdinal(earlier, later).Should().BeLessThan(0);
        SqliteDatabase.Parse(earlier).Should().Be(new DateTimeOffset(2026, 11, 3, 14, 0, 0, TimeSpan.Zero));
    }
}
