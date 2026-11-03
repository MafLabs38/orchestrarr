using System.Runtime.Versioning;

namespace Orchestrarr.Storage.Test;

public sealed class SqliteWriterLockTests : IDisposable
{
    private readonly TempDatabase _file = new();

    public void Dispose() => _file.Dispose();

    [Fact]
    public void A_second_host_on_the_same_file_is_refused_with_a_message_that_says_what_to_do()
    {
        _file.Open();
        using var first = SqliteWriterLock.Acquire(_file.Path);

        var second = () => SqliteWriterLock.Acquire(_file.Path);

        second.Should().Throw<InvalidOperationException>().WithMessage("*only one host may write it*Recreate*");
    }

    [Fact]
    public void The_lock_is_free_again_when_the_first_host_is_gone()
    {
        _file.Open();
        SqliteWriterLock.Acquire(_file.Path).Dispose();

        var again = () => SqliteWriterLock.Acquire(_file.Path).Dispose();

        again.Should().NotThrow();
    }

    [Fact]
    public void Two_different_databases_do_not_share_a_lock()
    {
        _file.Open();
        using var first = SqliteWriterLock.Acquire(_file.Path);
        var other = Path.Combine(Path.GetDirectoryName(_file.Path)!, "other.db");

        var second = () => SqliteWriterLock.Acquire(other).Dispose();

        second.Should().NotThrow();
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void The_lock_file_is_for_its_owner_only()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes");
        _file.Open();
        using var held = SqliteWriterLock.Acquire(_file.Path);

        File.GetUnixFileMode(SqliteWriterLock.LockPathOf(_file.Path)).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void The_lock_comes_first_it_makes_the_folder_and_leaves_the_database_alone()
    {
        var path = Path.Combine(Path.GetDirectoryName(_file.Path)!, "not-yet", "orchestrarr.db");

        using var held = SqliteWriterLock.Acquire(path);

        Directory.Exists(Path.GetDirectoryName(path)).Should().BeTrue();
        File.Exists(path).Should().BeFalse("a host that is refused, or that has not started yet, has written nothing");
    }
}
