using Orchestrarr.Host.Storage;

namespace Orchestrarr.Host.Test;

public sealed class StorageOptionsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "orchestrarr-root");

    [Fact]
    public void Memory_is_the_default_when_nobody_can_approve_and_ignores_the_path()
    {
        var settings = new StorageOptions { Path = "\0" }.Validate(Root, development: false, approving: false);

        settings.Should().BeOfType<MemoryStorage>();
        settings.Persistent.Should().BeFalse();
    }

    [Fact]
    public void A_relative_path_is_read_from_the_content_root()
    {
        var settings = new StorageOptions { Provider = StorageProvider.Sqlite, Path = "data/o.db" }.Validate(Root, development: true, approving: false);

        settings.Should().BeOfType<SqliteStorage>().Which.Path.Should().Be(Path.Combine(Root, "data", "o.db"));
        settings.Persistent.Should().BeTrue();
    }

    [Fact]
    public void An_absolute_path_is_kept()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "somewhere", "o.db");

        var settings = new StorageOptions { Provider = StorageProvider.Sqlite, Path = absolute }.Validate(Root, development: true, approving: false);

        settings.Should().BeOfType<SqliteStorage>().Which.Path.Should().Be(absolute);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_database_created_outside_development_starts_engaged(bool development, bool startsEngaged)
    {
        var settings = new StorageOptions { Provider = StorageProvider.Sqlite, Path = "o.db" }.Validate(Root, development, approving: true);

        settings.Should().BeOfType<SqliteStorage>().Which.StartEngagedWhenNew.Should().Be(startsEngaged);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\0b")]
    public void A_file_without_a_usable_path_is_an_error_and_never_memory(string path)
    {
        var validate = () => new StorageOptions { Provider = StorageProvider.Sqlite, Path = path }.Validate(Root, development: true, approving: false);

        validate.Should().Throw<InvalidOperationException>().WithMessage("*Storage:Path*");
    }

    [Fact]
    public void A_provider_that_does_not_exist_is_an_error()
    {
        var validate = () => new StorageOptions { Provider = (StorageProvider)7 }.Validate(Root, development: true, approving: false);

        validate.Should().Throw<InvalidOperationException>().WithMessage("*Storage:Provider*");
    }

    [Fact]
    public void Nothing_set_is_memory_in_development_where_nothing_is_kept_on_purpose()
    {
        new StorageOptions().Validate(Root, development: true, approving: true).Should().BeOfType<MemoryStorage>();
    }

    [Fact]
    public void Nothing_set_outside_development_with_approving_on_stops_the_host_so_that_a_restart_never_releases_the_stop_by_omission()
    {
        var validate = () => new StorageOptions().Validate(Root, development: false, approving: true);

        validate.Should().Throw<InvalidOperationException>().WithMessage("*Storage:Provider is not set*");
    }

    [Fact]
    public void Memory_asked_for_outside_development_is_allowed_because_it_was_asked_for()
    {
        new StorageOptions { Provider = StorageProvider.Memory }.Validate(Root, development: false, approving: true).Should().BeOfType<MemoryStorage>();
    }
}
